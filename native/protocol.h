#pragma once
#include <cstdint>
#include <cstddef>
#include <cwchar>

namespace taskbar {
constexpr std::uint32_t magic = 0x53544e31;
constexpr std::uint32_t version = 1;
constexpr unsigned readyMessage = 0x8051, inputMessage = 0x8052;
constexpr unsigned clickTimer = 1;
enum class Command : unsigned { toggle = 1, cancel, menu, open, scroll };

// Fixed-size WM_COPYDATA payload; no pointers, ABI-sized fields or executable commands.
struct View {
    std::uint32_t magic, version, bytes;
    std::int32_t x, y, width, height;
    std::uint32_t visible, state, accent, text, secondary, track, progress;
    wchar_t time[32], action[96];
};
static_assert(sizeof(View) == 312, "Managed/native protocol size changed");
inline bool valid(const View& view) noexcept {
    return view.magic == magic && view.version == version && view.bytes == sizeof(View)
        && view.visible <= 1 && view.state <= 2 && view.progress <= 1000000
        && view.width > 0 && view.width <= 1024 && view.height > 0 && view.height <= 256
        && std::wmemchr(view.time, L'\0', 32) && std::wmemchr(view.action, L'\0', 96);
}
}
