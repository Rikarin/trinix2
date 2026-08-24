using Vixen.Platform;

namespace Trinix.Platform;

/// <summary>
///     Turns the keycode Wayland delivers into the key Vixen names.
/// </summary>
/// <remarks>
///     <para>
///         Two numbering schemes that look alike and are not. Wayland carries Linux
///         evdev codes — <c>KEY_A</c> is 30 — and Vixen's <see cref="Key" /> is the USB
///         HID usage table, where A is 4. Both are physical positions, which is why the
///         translation is a table and never a keymap lookup: <see cref="Key" /> must
///         mean the same place under the same finger on every layout, and asking xkb
///         what a key "is" would make WASD move on AZERTY.
///     </para>
///     <para>
///         ⚠ <b>A wrong entry does not fail.</b> It reports a different key, and the
///         application binds an action to a position nobody can press. The table is
///         therefore written in evdev order, in the groups the kernel header declares
///         them, so that it can be read against
///         <c>include/uapi/linux/input-event-codes.h</c> line by line.
///     </para>
/// </remarks>
static class EvdevKeys {
    /// <summary>The highest evdev code this table covers.</summary>
    /// <remarks>
    ///     Codes above this exist — media keys, the ones a laptop puts on Fn — and none
    ///     of them has a HID usage on the keyboard page, so they translate to
    ///     <see cref="Key.Unknown" /> rather than to something arbitrary.
    /// </remarks>
    const int Highest = 194;

    static readonly byte[] Usages = BuildTable();

    /// <summary>Translates one evdev keycode.</summary>
    /// <param name="code">The code from the wire.</param>
    /// <returns>The key, or <see cref="Key.Unknown" /> for a code with no HID usage.</returns>
    public static Key Translate(uint code) =>
        code <= Highest ? (Key)Usages[code] : Key.Unknown;

    static byte[] BuildTable() {
        var table = new byte[Highest + 1];

        void Map(int evdev, Key key) => table[evdev] = (byte)key;

        // The first row, left to right, exactly as the kernel numbers them.
        Map(1, Key.Escape);
        Map(2, Key.Number1); Map(3, Key.Number2); Map(4, Key.Number3); Map(5, Key.Number4);
        Map(6, Key.Number5); Map(7, Key.Number6); Map(8, Key.Number7); Map(9, Key.Number8);
        Map(10, Key.Number9); Map(11, Key.Number0);
        Map(12, Key.Minus); Map(13, Key.Equals); Map(14, Key.Backspace); Map(15, Key.Tab);

        // QWERTY row, then the home row, then the bottom row. The names are the
        // positions, which is the whole point of this enum.
        Map(16, Key.Q); Map(17, Key.W); Map(18, Key.E); Map(19, Key.R); Map(20, Key.T);
        Map(21, Key.Y); Map(22, Key.U); Map(23, Key.I); Map(24, Key.O); Map(25, Key.P);
        Map(26, Key.LeftBracket); Map(27, Key.RightBracket); Map(28, Key.Enter);

        Map(29, Key.LeftControl);
        Map(30, Key.A); Map(31, Key.S); Map(32, Key.D); Map(33, Key.F); Map(34, Key.G);
        Map(35, Key.H); Map(36, Key.J); Map(37, Key.K); Map(38, Key.L);
        Map(39, Key.Semicolon); Map(40, Key.Apostrophe); Map(41, Key.Grave);

        Map(42, Key.LeftShift); Map(43, Key.Backslash);
        Map(44, Key.Z); Map(45, Key.X); Map(46, Key.C); Map(47, Key.V); Map(48, Key.B);
        Map(49, Key.N); Map(50, Key.M);
        Map(51, Key.Comma); Map(52, Key.Period); Map(53, Key.Slash); Map(54, Key.RightShift);

        Map(55, Key.KeypadMultiply); Map(56, Key.LeftAlt); Map(57, Key.Space); Map(58, Key.CapsLock);

        Map(59, Key.F1); Map(60, Key.F2); Map(61, Key.F3); Map(62, Key.F4); Map(63, Key.F5);
        Map(64, Key.F6); Map(65, Key.F7); Map(66, Key.F8); Map(67, Key.F9); Map(68, Key.F10);

        Map(69, Key.NumLock); Map(70, Key.ScrollLock);

        // ⚠ The keypad is not in numeric order in either scheme, and the two
        // disorders are different: evdev counts down the rows from 7, HID counts
        // up from 1. Transcribing it as a range is the mistake this table exists
        // to have already made.
        Map(71, Key.Keypad7); Map(72, Key.Keypad8); Map(73, Key.Keypad9); Map(74, Key.KeypadMinus);
        Map(75, Key.Keypad4); Map(76, Key.Keypad5); Map(77, Key.Keypad6); Map(78, Key.KeypadPlus);
        Map(79, Key.Keypad1); Map(80, Key.Keypad2); Map(81, Key.Keypad3);
        Map(82, Key.Keypad0); Map(83, Key.KeypadPeriod);

        Map(86, Key.NonUsBackslash);
        Map(87, Key.F11); Map(88, Key.F12);

        Map(96, Key.KeypadEnter); Map(97, Key.RightControl); Map(98, Key.KeypadDivide);
        Map(99, Key.PrintScreen); Map(100, Key.RightAlt);

        Map(102, Key.Home); Map(103, Key.Up); Map(104, Key.PageUp);
        Map(105, Key.Left); Map(106, Key.Right);
        Map(107, Key.End); Map(108, Key.Down); Map(109, Key.PageDown);
        Map(110, Key.Insert); Map(111, Key.Delete);

        Map(119, Key.Pause);
        Map(125, Key.LeftMeta); Map(126, Key.RightMeta); Map(127, Key.Application);

        // The extended function keys, which exist on very few keyboards and on
        // every remote-desktop client.
        Map(183, Key.F13); Map(184, Key.F14); Map(185, Key.F15); Map(186, Key.F16);
        Map(187, Key.F17); Map(188, Key.F18); Map(189, Key.F19); Map(190, Key.F20);
        Map(191, Key.F21); Map(192, Key.F22); Map(193, Key.F23); Map(194, Key.F24);

        return table;
    }
}
