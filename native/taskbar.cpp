#include <windows.h>
#include <windowsx.h>
#include <shellapi.h>
#include <d2d1.h>
#include <dwrite.h>
#include <wrl/client.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <cerrno>
#include <limits>
#include "protocol.h"

using Microsoft::WRL::ComPtr;
using taskbar::View;
namespace {
constexpr wchar_t windowClass[] = L"SleepTimer.NativeTaskbar.1";
struct Surface {
    HDC dc = nullptr;
    HBITMAP bitmap = nullptr;
    HGDIOBJ original = nullptr;
    void* pixels = nullptr;
    int width = 0, height = 0;
    ~Surface() { reset(); }
    void reset() noexcept {
        if (dc && original && original != HGDI_ERROR) SelectObject(dc, original);
        if (bitmap) DeleteObject(bitmap);
        if (dc) DeleteDC(dc);
        dc = nullptr; bitmap = nullptr; original = nullptr; pixels = nullptr;
    }
    bool resize(int w, int h) noexcept {
        if (dc && width == w && height == h) return true;
        reset(); width = w; height = h;
        BITMAPINFO info{};
        info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = w; info.bmiHeader.biHeight = -h;
        info.bmiHeader.biPlanes = 1; info.bmiHeader.biBitCount = 32; info.bmiHeader.biCompression = BI_RGB;
        dc = CreateCompatibleDC(nullptr);
        if (!dc) return false;
        bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &pixels, nullptr, 0);
        if (!bitmap) { reset(); return false; }
        original = SelectObject(dc, bitmap);
        return original != nullptr && original != HGDI_ERROR;
    }
};
D2D1_COLOR_F color(std::uint32_t rgb) noexcept {
    return D2D1::ColorF(static_cast<float>(rgb >> 16 & 255) / 255.f,
        static_cast<float>(rgb >> 8 & 255) / 255.f, static_cast<float>(rgb & 255) / 255.f);
}
struct Renderer {
    Surface surface;
    ComPtr<ID2D1Factory> factory;
    ComPtr<ID2D1DCRenderTarget> target;
    ComPtr<IDWriteFactory> writing;
    ComPtr<IDWriteTextFormat> timeFormat, actionFormat;
    bool init() noexcept {
        if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, factory.GetAddressOf()))) return false;
        if (FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
            reinterpret_cast<IUnknown**>(writing.GetAddressOf())))) return false;
        if (FAILED(writing->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD,
            DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 12, L"", timeFormat.GetAddressOf()))) return false;
        if (FAILED(writing->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL,
            DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 11, L"", actionFormat.GetAddressOf()))) return false;
        for (auto format : {timeFormat.Get(), actionFormat.Get()}) {
            format->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_TRAILING);
            format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
            format->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
            ComPtr<IDWriteInlineObject> ellipsis;
            if (SUCCEEDED(writing->CreateEllipsisTrimmingSign(format, ellipsis.GetAddressOf()))) {
                DWRITE_TRIMMING trimming{DWRITE_TRIMMING_GRANULARITY_CHARACTER, 0, 0};
                format->SetTrimming(&trimming, ellipsis.Get());
            }
        }
        return true;
    }
    bool draw(const View& view, float scale) noexcept {
        if (!surface.resize(view.width, view.height)) return false;
        if (!target) {
            auto properties = D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_SOFTWARE,
                D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
            if (FAILED(factory->CreateDCRenderTarget(&properties, target.GetAddressOf()))) return false;
            target->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        }
        RECT pixels{0, 0, view.width, view.height};
        if (FAILED(target->BindDC(surface.dc, &pixels))) return false;
        target->SetTransform(D2D1::Matrix3x2F::Scale(scale, scale));
        target->BeginDraw();
        // Preserve hit testing over blank padding with an imperceptible alpha-1 background.
        target->Clear(D2D1::ColorF(0, 0, 0, 1 / 255.f));
        ComPtr<ID2D1SolidColorBrush> ink;
        if (FAILED(target->CreateSolidColorBrush(color(view.track), ink.GetAddressOf()))) {
            target->EndDraw(); return false;
        }
        float width = static_cast<float>(view.width) / scale, height = static_cast<float>(view.height) / scale;
        auto center = D2D1::Point2F(width - 19, height / 2);
        target->DrawEllipse(D2D1::Ellipse(center, 11.5f, 11.5f), ink.Get(), 3);
        ink->SetColor(color(view.accent));
        float fraction = static_cast<float>(view.progress) / 1000000.f;
        if (fraction >= .999f) target->DrawEllipse(D2D1::Ellipse(center, 11.5f, 11.5f), ink.Get(), 3);
        else if (fraction > 0) {
            ComPtr<ID2D1PathGeometry> path;
            ComPtr<ID2D1GeometrySink> sink;
            if (SUCCEEDED(factory->CreatePathGeometry(path.GetAddressOf())) && SUCCEEDED(path->Open(sink.GetAddressOf()))) {
                float angle = fraction * 6.28318530718f;
                sink->BeginFigure(D2D1::Point2F(center.x, center.y - 11.5f), D2D1_FIGURE_BEGIN_HOLLOW);
                sink->AddArc(D2D1::ArcSegment(D2D1::Point2F(center.x + 11.5f * std::sin(angle), center.y - 11.5f * std::cos(angle)),
                    D2D1::SizeF(11.5f, 11.5f), 0, D2D1_SWEEP_DIRECTION_CLOCKWISE,
                    fraction > .5f ? D2D1_ARC_SIZE_LARGE : D2D1_ARC_SIZE_SMALL));
                sink->EndFigure(D2D1_FIGURE_END_OPEN);
                if (SUCCEEDED(sink->Close())) target->DrawGeometry(path.Get(), ink.Get(), 3);
            }
        }
        if (view.state == 2) {
            target->DrawLine(D2D1::Point2F(center.x - 2.5f, center.y - 5), D2D1::Point2F(center.x - 2.5f, center.y + 5), ink.Get(), 3);
            target->DrawLine(D2D1::Point2F(center.x + 2.5f, center.y - 5), D2D1::Point2F(center.x + 2.5f, center.y + 5), ink.Get(), 3);
        }
        float middle = height / 2;
        ink->SetColor(color(view.state == 0 ? view.secondary : view.text));
        target->DrawText(view.time, static_cast<UINT32>(std::wcslen(view.time)), timeFormat.Get(),
            D2D1::RectF(5, middle - 14, width - 39, middle), ink.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP);
        ink->SetColor(color(view.secondary));
        target->DrawText(view.action, static_cast<UINT32>(std::wcslen(view.action)), actionFormat.Get(),
            D2D1::RectF(5, middle, width - 39, middle + 14), ink.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP);
        HRESULT result = target->EndDraw();
        if (result == D2DERR_RECREATE_TARGET) target.Reset();
        return SUCCEEDED(result);
    }
};

struct Host {
    HWND owner = nullptr, parent = nullptr, window = nullptr;
    Renderer renderer;
    View previous{};
    bool hasView = false, suppressRelease = false, clickPending = false;
    float previousScale = 0;

    void input(taskbar::Command command, short delta = 0) noexcept {
        KillTimer(window, taskbar::clickTimer);
        clickPending = false;
        unsigned modifiers = ((GetKeyState(VK_MENU) & 0x8000) ? 1u : 0u)
            | ((GetKeyState(VK_CONTROL) & 0x8000) ? 2u : 0u) | ((GetKeyState(VK_SHIFT) & 0x8000) ? 4u : 0u);
        auto encoded = static_cast<std::uint32_t>(command) | (modifiers << 8)
            | (static_cast<std::uint32_t>(static_cast<std::uint16_t>(delta)) << 16);
        PostMessageW(owner, taskbar::inputMessage, reinterpret_cast<WPARAM>(window), static_cast<LPARAM>(encoded));
    }
    bool update(const View& view) noexcept {
        if (!taskbar::valid(view)) return false;
        if (!view.visible) { KillTimer(window, taskbar::clickTimer); clickPending = false; ShowWindow(window, SW_HIDE); return true; }
        if (!IsWindow(parent) || !IsWindow(owner)) return false;
        POINT position{view.x, view.y};
        if (!ScreenToClient(parent, &position)) return false;
        RECT client{};
        if (!GetClientRect(parent, &client) || position.x < 0 || position.y < 0
            || position.x > client.right - view.width || position.y > client.bottom - view.height) {
            ShowWindow(window, SW_HIDE); return true; // Stale shell layout: never draw outside the taskbar.
        }
        float scale = static_cast<float>(std::max(96u, GetDpiForWindow(parent))) / 96.f;
        bool redraw = !hasView || previousScale != scale || previous.width != view.width || previous.height != view.height
            || std::memcmp(&previous.state, &view.state, sizeof(View) - offsetof(View, state)) != 0;
        if (redraw) {
            if (!renderer.draw(view, scale)) return false;
            POINT source{0, 0}; SIZE size{view.width, view.height};
            BLENDFUNCTION blend{AC_SRC_OVER, 0, 255, AC_SRC_ALPHA};
            // Position a child in parent client coordinates with SetWindowPos below. No desktop-sized surface.
            if (!UpdateLayeredWindow(window, nullptr, nullptr, &size, renderer.surface.dc, &source, 0, &blend, ULW_ALPHA)) return false;
        }
        if (!hasView || previous.x != view.x || previous.y != view.y || previous.width != view.width
            || previous.height != view.height || !IsWindowVisible(window)) {
            if (!SetWindowPos(window, HWND_TOP, position.x, position.y, view.width, view.height, SWP_NOACTIVATE | SWP_SHOWWINDOW)) return false;
        }
        previous = view; previousScale = scale; hasView = true;
        return true;
    }
};

LRESULT CALLBACK windowProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam) noexcept {
    auto host = reinterpret_cast<Host*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        host = static_cast<Host*>(reinterpret_cast<CREATESTRUCTW*>(lParam)->lpCreateParams);
        host->window = window; SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(host));
    }
    if (!host) return DefWindowProcW(window, message, wParam, lParam);
    switch (message) {
    case WM_COPYDATA: {
        auto packet = reinterpret_cast<const COPYDATASTRUCT*>(lParam);
        if (reinterpret_cast<HWND>(wParam) != host->owner || !packet || packet->dwData != taskbar::magic
            || packet->cbData != sizeof(View) || !packet->lpData) return 0;
        View view{}; std::memcpy(&view, packet->lpData, sizeof(view));
        return host->update(view) ? 1 : 0;
    }
    case WM_MOUSEACTIVATE: return MA_NOACTIVATE;
    case WM_LBUTTONDBLCLK: host->suppressRelease = true; host->input(taskbar::Command::open); return 0;
    case WM_LBUTTONUP:
        if (host->suppressRelease) host->suppressRelease = false;
        else { host->clickPending = true; SetTimer(window, taskbar::clickTimer, GetDoubleClickTime(), nullptr); }
        return 0;
    case WM_RBUTTONUP: host->input(taskbar::Command::cancel); return 0;
    case WM_MBUTTONUP: host->input(taskbar::Command::menu); return 0;
    case WM_MOUSEWHEEL: host->input(taskbar::Command::scroll, GET_WHEEL_DELTA_WPARAM(wParam)); return 0;
    case WM_TIMER:
        if (wParam == taskbar::clickTimer && host->clickPending) host->input(taskbar::Command::toggle);
        return 0;
    case WM_ERASEBKGND: return 1;
    case WM_CLOSE: DestroyWindow(window); return 0;
    case WM_DESTROY: PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(window, message, wParam, lParam);
}

int selfTest() {
    View view{};
    view.magic = taskbar::magic; view.version = taskbar::version; view.bytes = sizeof(View);
    view.width = 140; view.height = 34; view.visible = 1; view.progress = 750000;
    view.accent = 0x0cd29d; view.text = 0xededed; view.secondary = 0x999999; view.track = 0x323232;
    wcscpy_s(view.time, L"01:30:00"); wcscpy_s(view.action, L"Shut down");
    if (!taskbar::valid(view)) return 10;
    auto invalid = view; invalid.version++; if (taskbar::valid(invalid)) return 11;
    invalid = view; invalid.width = 100000; if (taskbar::valid(invalid)) return 12;
    invalid = view; invalid.progress = 1000001; if (taskbar::valid(invalid)) return 13;
    invalid = view; std::wmemset(invalid.time, L'x', 32); if (taskbar::valid(invalid)) return 14;
    Renderer renderer;
    if (!renderer.init()) return 15;
    for (int scale : {1, 2, 3}) {
        view.width = 140 * scale; view.height = 34 * scale;
        if (!renderer.draw(view, static_cast<float>(scale))) return 16;
        auto pixels = static_cast<const std::uint32_t*>(renderer.surface.pixels);
        if ((pixels[0] >> 24) != 1) return 17;
        bool ink = false;
        for (int i = 0; i < view.width * view.height; i++) if ((pixels[i] >> 24) > 128) ink = true;
        if (!ink) return 18;
    }
    return 0;
}
HWND parseHandle(const wchar_t* text) noexcept {
    if (!text || !*text || *text == L'-') return nullptr;
    errno = 0; wchar_t* end = nullptr;
    auto value = std::wcstoull(text, &end, 16);
    return errno || *end || value > std::numeric_limits<std::uintptr_t>::max()
        ? nullptr : reinterpret_cast<HWND>(static_cast<std::uintptr_t>(value));
}
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int) {
    int count = 0;
    auto arguments = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!arguments) return 2;
    if (count == 2 && std::wcscmp(arguments[1], L"--self-test") == 0) { LocalFree(arguments); return selfTest(); }
    HWND owner = count == 3 ? parseHandle(arguments[1]) : nullptr;
    HWND parent = count == 3 ? parseHandle(arguments[2]) : nullptr;
    LocalFree(arguments);
    DWORD ownerProcess = 0;
    GetWindowThreadProcessId(owner, &ownerProcess);
    if (!ownerProcess || !IsWindow(parent) || !IsWindow(owner) || parent == owner) return 3;
    HANDLE alive = OpenProcess(SYNCHRONIZE, FALSE, ownerProcess);
    if (!alive) return 4;
    // Match Explorer's awareness before creating a window. WPF's DPI context is never modified.
    auto context = GetWindowDpiAwarenessContext(parent);
    if (!context || !SetThreadDpiAwarenessContext(context)) { CloseHandle(alive); return 5; }
    Host host; host.owner = owner; host.parent = parent;
    if (!host.renderer.init()) { CloseHandle(alive); return 6; }
    WNDCLASSW type{};
    type.style = CS_DBLCLKS; type.lpfnWndProc = windowProcedure; type.hInstance = instance;
    type.hCursor = LoadCursorW(nullptr, IDC_HAND); type.lpszClassName = windowClass;
    if (!RegisterClassW(&type)) { CloseHandle(alive); return 7; }
    auto window = CreateWindowExW(WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, windowClass, L"Sleep Timer",
        WS_CHILD, 0, 0, 140, 34, parent, nullptr, instance, &host);
    if (!window || GetParent(window) != parent) { CloseHandle(alive); return 8; }
    PostMessageW(owner, taskbar::readyMessage, reinterpret_cast<WPARAM>(window), taskbar::version);
    bool done = false;
    while (!done) {
        DWORD result = MsgWaitForMultipleObjects(1, &alive, FALSE, INFINITE, QS_ALLINPUT);
        if (result != WAIT_OBJECT_0 + 1) break;
        MSG message{};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) {
            if (message.message == WM_QUIT) { done = true; break; }
            TranslateMessage(&message); DispatchMessageW(&message);
        }
    }
    if (IsWindow(window)) DestroyWindow(window);
    CloseHandle(alive);
    return 0;
}
