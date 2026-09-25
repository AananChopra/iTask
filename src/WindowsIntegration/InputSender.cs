using System.Runtime.InteropServices;
using static iTask.WindowsIntegration.NativeMethods;

namespace iTask.WindowsIntegration;

/// <summary>Synthesizes keyboard shortcuts via SendInput (used to reach Start, Notification Center, …).</summary>
public static class InputSender
{
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_A = 0x41;
    public const ushort VK_MENU = 0x12; // Alt

    private static bool IsExtended(ushort vk) => vk is VK_LWIN or 0x5C /* RWIN */ or 0x5D /* APPS */;

    /// <summary>Presses the keys in order and releases them in reverse order.</summary>
    public static bool SendChord(params ushort[] keys)
    {
        var inputs = new INPUT[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++)
        {
            inputs[i] = Key(keys[i], up: false);
            inputs[inputs.Length - 1 - i] = Key(keys[i], up: true);
        }
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0),
            },
        },
    };
}
