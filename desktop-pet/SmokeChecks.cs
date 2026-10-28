using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopPet;

// Invoke on the UI thread of the isolated, non-persisting smoke-test instance.
// This checks the real HWND without generating any keyboard or mouse input.
internal static class SmokeChecks
{
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExLayered = 0x00080000L;
    private const long WsExNoActivate = 0x08000000L;
    private const long CheckedFlags = WsExTopmost | WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate;

    public static object VerifyWindow(PetWindow pet)
    {
        if (pet == null) throw new ArgumentNullException(nameof(pet));
        pet.Dispatcher.VerifyAccess();
        var hwnd = new WindowInteropHelper(pet).Handle;
        Require(hwnd != IntPtr.Zero && IsWindow(hwnd) && pet.IsLoaded, "PetWindow must have a loaded native HWND.");

        double originalSize = pet.Settings.Size;
        bool originalTopmost = pet.Topmost;
        bool originalClickThrough = pet.Settings.ClickThrough;
        bool originalVisible = pet.IsVisible;
        double? originalSavedLeft = pet.Settings.Left;
        double? originalSavedTop = pet.Settings.Top;
        string? originalSavedMonitor = pet.Settings.DpiMonitor;
        var originalBounds = NativeMethods.GetWindowBoundsPixels(hwnd);
        long initialStyle = ReadStyle(hwnd);
        Require(Has(initialStyle, WsExLayered), "WS_EX_LAYERED is missing from the transparent pet HWND.");
        Require(Has(initialStyle, WsExToolWindow), "WS_EX_TOOLWINDOW is missing from the pet HWND.");
        Require(Has(initialStyle, WsExNoActivate), "WS_EX_NOACTIVATE is missing from the pet HWND.");
        Require(Has(initialStyle, WsExTopmost) == originalTopmost, "Initial native topmost flag disagrees with the WPF setting.");
        Require(Has(initialStyle, WsExTransparent) == originalClickThrough, "Initial native click-through flag disagrees with the saved setting.");

        long transparentOn = 0, transparentOff = 0, topmostOn = 0, topmostOff = 0;
        Rect beforeHide = Rect.Empty, afterShow = Rect.Empty, work = Rect.Empty;
        string monitor = NativeMethods.GetMonitorName(hwnd);
        double dpiScale = NativeMethods.GetDpiScale(hwnd);
        try
        {
            if (!pet.IsVisible) pet.Show();
            pet.SetClickThrough(true);
            transparentOn = ReadStyle(hwnd);
            Require(Has(transparentOn, WsExTransparent), "Enabling click-through did not set WS_EX_TRANSPARENT.");
            Require(Has(transparentOn, WsExLayered), "Enabling click-through removed WS_EX_LAYERED.");
            pet.SetClickThrough(false);
            transparentOff = ReadStyle(hwnd);
            Require(!Has(transparentOff, WsExTransparent), "Disabling click-through did not clear WS_EX_TRANSPARENT.");
            Require(Has(transparentOff, WsExLayered), "Disabling click-through removed WS_EX_LAYERED.");

            pet.SetTopmost(false);
            topmostOff = ReadStyle(hwnd);
            Require(!Has(topmostOff, WsExTopmost), "Disabling topmost did not clear the native topmost flag.");
            pet.SetTopmost(true);
            topmostOn = ReadStyle(hwnd);
            Require(Has(topmostOn, WsExTopmost), "Enabling topmost did not set the native topmost flag.");

            // The smallest supported size leaves a measurable interior position even
            // on compact/high-DPI displays. The original size is restored below.
            pet.SetSize(160);
            pet.UpdateLayout();
            work = NativeMethods.GetWindowWorkAreaPixels(hwnd);
            var testBounds = NativeMethods.GetWindowBoundsPixels(hwnd);
            double horizontalRoom = work.Width - testBounds.Width;
            double verticalRoom = work.Height - testBounds.Height;
            Require(horizontalRoom >= 4 && verticalRoom >= 4, "The monitor has no interior area large enough for the smallest pet window.");
            int left = (int)Math.Round(work.Left + horizontalRoom * 0.37);
            int top = (int)Math.Round(work.Top + verticalRoom * 0.41);
            NativeMethods.SetWindowPositionPixels(hwnd, left, top);
            pet.ClampPosition();
            beforeHide = NativeMethods.GetWindowBoundsPixels(hwnd);
            Require(Math.Abs(beforeHide.Left - left) <= 1 && Math.Abs(beforeHide.Top - top) <= 1,
                "The native pet window did not move to the requested interior position.");
            Require(beforeHide.Left > work.Left && beforeHide.Top > work.Top && beforeHide.Right < work.Right && beforeHide.Bottom < work.Bottom,
                "The placement test did not produce a non-corner position inside the work area.");
            pet.Hide();
            Require(!pet.IsVisible && !IsWindowVisible(hwnd), "Hide left the native pet HWND visible.");
            pet.Show();
            pet.ClampPosition();
            afterShow = NativeMethods.GetWindowBoundsPixels(hwnd);
            Require(pet.IsVisible && IsWindowVisible(hwnd), "Show did not restore native pet HWND visibility.");
            Require(SameBounds(beforeHide, afterShow), "Hiding and showing the pet changed its saved on-screen position or size.");
            long afterShowStyle = ReadStyle(hwnd);
            Require(Has(afterShowStyle, WsExLayered | WsExToolWindow | WsExNoActivate),
                "Hide/show removed one of the required layered/toolwindow/noactivate flags.");
        }
        finally
        {
            pet.SetSize(originalSize);
            pet.SetTopmost(originalTopmost);
            pet.SetClickThrough(originalClickThrough);
            NativeMethods.SetWindowPositionPixels(hwnd, (int)Math.Round(originalBounds.Left), (int)Math.Round(originalBounds.Top));
            if (originalVisible) pet.Show(); else pet.Hide();
            pet.Settings.Left = originalSavedLeft;
            pet.Settings.Top = originalSavedTop;
            pet.Settings.DpiMonitor = originalSavedMonitor;
        }

        var restoredBounds = NativeMethods.GetWindowBoundsPixels(hwnd);
        long restoredStyle = ReadStyle(hwnd);
        Require(SameBounds(originalBounds, restoredBounds), "The smoke check failed to restore the original pet placement and size.");
        Require((restoredStyle & CheckedFlags) == (initialStyle & CheckedFlags), "The smoke check failed to restore the original native flags.");
        Require(pet.IsVisible == originalVisible && IsWindowVisible(hwnd) == originalVisible, "The smoke check failed to restore the original visibility.");
        Require(Math.Abs(pet.Settings.Size - originalSize) < 0.01, "The smoke check failed to restore the original configured size.");

        return new
        {
            passed = true,
            checks = new { layered = true, toolWindow = true, noActivate = true, clickThroughEnabled = true, clickThroughDisabled = true, topmostEnabled = true, topmostDisabled = true, hiddenNativeWindow = true, visibleNativeWindow = true, hideShowPreservesInteriorPlacement = true, restoredOriginalState = true },
            monitor,
            dpiScale,
            nativeFlags = new { initial = Hex(initialStyle), transparentOn = Hex(transparentOn), transparentOff = Hex(transparentOff), topmostOn = Hex(topmostOn), topmostOff = Hex(topmostOff), restored = Hex(restoredStyle) },
            placement = new { units = "physical pixels", workArea = Coordinates(work), beforeHide = Coordinates(beforeHide), afterShow = Coordinates(afterShow), original = Coordinates(originalBounds), restored = Coordinates(restoredBounds) }
        };
    }

    private static bool Has(long style, long flags) => (style & flags) == flags;
    private static long ReadStyle(IntPtr hwnd) => GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
    private static string Hex(long value) => "0x" + value.ToString("X8");
    private static object Coordinates(Rect value) => new { left = value.Left, top = value.Top, width = value.Width, height = value.Height };
    private static bool SameBounds(Rect first, Rect second) => Math.Abs(first.Left - second.Left) <= 1 && Math.Abs(first.Top - second.Top) <= 1 && Math.Abs(first.Width - second.Width) <= 1 && Math.Abs(first.Height - second.Height) <= 1;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Native window smoke check: " + message);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);
}
