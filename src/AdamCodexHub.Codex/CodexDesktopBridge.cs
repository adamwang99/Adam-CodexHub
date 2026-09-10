using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AdamCodexHub.Codex;

/// <summary>
/// Drives the Codex Desktop window from outside the app.
///
/// Codex Desktop keeps every thread bound to the provider it was created with, so after a provider
/// switch the old chat still answers from the ChatGPT account (usage limit) while the hub's overlay
/// only applies to new chats. The hub therefore opens Codex on the current project, asks the app
/// for a NEW chat (<c>Ctrl+N</c> — the app's own <c>codex.command.newThread</c> accelerator) and
/// types the handoff recap into the composer, leaving the user to press Enter.
///
/// Everything here is deliberately additive: no Codex file is modified, and the plain app
/// activation is still used when the CLI is unavailable.
/// </summary>
public static class CodexDesktopBridge
{
    /// <summary>AppUserModelId of the installed Codex Desktop package.</summary>
    private const string DesktopAppId = "OpenAI.Codex_2p2nqsd0c76g0!App";

    private static readonly string[] DesktopProcessNames = { "ChatGPT", "CodexDesktop" };

    private const byte VkControl = 0x11;
    private const byte VkMenu = 0x12;
    private const byte VkN = 0x4E;
    private const byte VkV = 0x56;
    private const byte VkReturn = 0x0D;
    private const uint KeyEventKeyUp = 0x0002;
    private const int MaxSubmitAttempts = 6;
    private const int SubmitGraceChecks = 6;
    private const int RefocusDelayMilliseconds = 600;
    private const int PasteSettleMilliseconds = 2500;
    private const int SubmitSettleMilliseconds = 1500;
    private const int SwRestore = 9;
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    /// <summary>Path to the newest installed Codex CLI, or null when it is not installed.</summary>
    public static string? ResolveCli()
    {
        var binRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
            "Codex",
            "bin");

        if (!Directory.Exists(binRoot))
        {
            return null;
        }

        try
        {
            return Directory
                .EnumerateDirectories(binRoot)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .Select(directory => Path.Combine(directory, "codex.exe"))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens Codex Desktop on <paramref name="workspacePath"/> (<c>codex app &lt;path&gt;</c>), so the
    /// app lands in the project the user was working in. Falls back to a plain app activation.
    /// </summary>
    public static bool OpenWorkspace(string? workspacePath)
    {
        var cli = ResolveCli();
        if (cli is not null && !string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
        {
            try
            {
                var startInfo = new ProcessStartInfo(cli)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("app");
                startInfo.ArgumentList.Add(workspacePath!);
                using var process = Process.Start(startInfo);
                return true;
            }
            catch (Exception)
            {
                // Fall through to the plain activation below.
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"shell:AppsFolder\\{DesktopAppId}\"")
            {
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for the Desktop window, opens a fresh chat, pastes <paramref name="handoffText"/>
    /// into the composer and submits it with Enter, so the continuation starts on its own.
    /// <paramref name="verifySent"/> reports whether the message reached Codex's session log;
    /// while it reports false, Enter is retried.
    /// Returns false when the recap could not be handed over (it then sits in the composer).
    /// </summary>
    public static async Task<bool> StartFreshChatAsync(
        string? handoffText,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        bool autoSubmit = true,
        Func<bool>? verifySent = null)
    {
        var window = await WaitForDesktopWindowAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (window == IntPtr.Zero || !TryFocus(window))
        {
            return false;
        }

        SendCtrlKey(VkN);

        // Give the new chat route time to mount before typing into its composer.
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(handoffText))
        {
            return true;
        }

        // Focus can drift while the app re-renders; re-assert it before pasting.
        if (!IsForeground(window) && !TryFocus(window))
        {
            return false;
        }

        if (!SetClipboardText(handoffText!))
        {
            return false;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        SendCtrlKey(VkV);

        if (!autoSubmit)
        {
            return true;
        }

        // A long paste needs a moment to reach the composer's state; Enter before that
        // submits nothing at all.
        await Task.Delay(TimeSpan.FromMilliseconds(PasteSettleMilliseconds), cancellationToken)
            .ConfigureAwait(false);

        for (var attempt = 0; attempt < MaxSubmitAttempts; attempt++)
        {
            // Enter is a keystroke: it only lands in the composer while this window owns focus.
            if (!IsForeground(window) && !TryFocus(window))
            {
                // Focus can bounce away while the paste renders; come back instead of stranding
                // the recap half-sent.
                await Task.Delay(TimeSpan.FromMilliseconds(RefocusDelayMilliseconds), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            SendKey(VkReturn);
            await Task.Delay(TimeSpan.FromMilliseconds(SubmitSettleMilliseconds), cancellationToken)
                .ConfigureAwait(false);

            if (verifySent is null || SafeVerify(verifySent))
            {
                return true;
            }
        }

        if (verifySent is null)
        {
            return true;
        }

        // A live session writes its rollout a beat after the send, so take one last look before
        // telling the caller the recap never left the composer.
        for (var grace = 0; grace < SubmitGraceChecks; grace++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(SubmitSettleMilliseconds), cancellationToken)
                .ConfigureAwait(false);

            if (SafeVerify(verifySent))
            {
                return true;
            }
        }

        // The recap is sitting in the composer, so the user only has to press Enter.
        return false;
    }

    /// <summary>
    /// A single-line slice of the recap, used as the marker for the delivery check. Line breaks
    /// would never match a substring, but quotes and backslashes are kept: the check compares the
    /// decoded message text, where they are part of the content.
    /// </summary>
    public static string BuildMarker(string text, int length = 32)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var character in text)
        {
            if (character is '\r' or '\n')
            {
                continue;
            }

            builder.Append(character);
            if (builder.Length >= length)
            {
                break;
            }
        }

        return builder.ToString().Trim();
    }

    private static bool SafeVerify(Func<bool> verify)
    {
        try
        {
            return verify();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static async Task<IntPtr> WaitForDesktopWindowAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var window = FindDesktopWindow();
            if (window != IntPtr.Zero)
            {
                return window;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return IntPtr.Zero;
            }
        }

        return IntPtr.Zero;
    }

    public static IntPtr FindDesktopWindow()
    {
        foreach (var name in DesktopProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    var handle = process.MainWindowHandle;
                    if (handle != IntPtr.Zero && IsWindowVisible(handle))
                    {
                        return handle;
                    }
                }
                catch (Exception)
                {
                    // Process exited between enumeration and inspection.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return IntPtr.Zero;
    }

    public static bool IsForeground(IntPtr window) => GetForegroundWindow() == window;

    /// <summary>
    /// Brings a window to the foreground. Windows normally refuses this for a background process,
    /// but a synthetic ALT tap marks this thread as the last input one, which unlocks it.
    /// </summary>
    public static bool TryFocus(IntPtr window)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ShowWindow(window, SwRestore);

            keybd_event(VkMenu, 0, 0, IntPtr.Zero);
            keybd_event(VkMenu, 0, KeyEventKeyUp, IntPtr.Zero);
            Thread.Sleep(120);

            SetForegroundWindow(window);
            Thread.Sleep(400);

            if (IsForeground(window))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Sends Ctrl+&lt;virtualKey&gt; to the focused window.</summary>
    public static void SendCtrlKey(byte virtualKey)
    {
        keybd_event(VkControl, 0, 0, IntPtr.Zero);
        Thread.Sleep(40);
        keybd_event(virtualKey, 0, 0, IntPtr.Zero);
        keybd_event(virtualKey, 0, KeyEventKeyUp, IntPtr.Zero);
        Thread.Sleep(40);
        keybd_event(VkControl, 0, KeyEventKeyUp, IntPtr.Zero);
    }

    /// <summary>Sends a single key press to the focused window.</summary>
    public static void SendKey(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, IntPtr.Zero);
        Thread.Sleep(40);
        keybd_event(virtualKey, 0, KeyEventKeyUp, IntPtr.Zero);
    }

    public static bool SetClipboardText(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(150);
                continue;
            }

            var handle = IntPtr.Zero;
            try
            {
                EmptyClipboard();

                // The clipboard takes ownership of GlobalAlloc(GMEM_MOVEABLE) memory, so the block
                // is intentionally not freed on success.
                handle = GlobalAlloc(GmemMoveable, (UIntPtr)((text.Length + 1) * sizeof(char)));
                if (handle == IntPtr.Zero)
                {
                    return false;
                }

                var target = GlobalLock(handle);
                if (target == IntPtr.Zero)
                {
                    GlobalFree(handle);
                    return false;
                }

                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * sizeof(char), 0);
                GlobalUnlock(handle);

                if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
                {
                    GlobalFree(handle);
                    return false;
                }

                handle = IntPtr.Zero; // owned by the clipboard now
                return true;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    GlobalFree(handle);
                }

                CloseClipboard();
            }
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, IntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint format, IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr handle);
}
