using Trinix.Platform;
using Vixen.Core.Mathematics;
using Vixen.Ui.Composition;
using Vixen.Ui.Desktop;

namespace Trinix.Apps.HelloUi;

/// <summary>
///     A Vixen application, in a window, on Trinix.
/// </summary>
/// <remarks>
///     <para>
///         What this proves is a chain, and every link in it was built separately:
///         Trinix's compositor gives out a Wayland display; <c>trinix-wl-client</c>
///         turns it into a window; <see cref="TrinixPlatform" /> presents that window
///         as Vixen's <c>IPlatform</c>; Vixen builds a Vulkan device on it, which is
///         lavapipe, which rasterises on the processor; and the swapchain that comes
///         back presents through <c>wl_shm</c> to the compositor it started from.
///     </para>
///     <para>
///         <b>One line is the whole integration.</b> <c>Platform = …</c> below is the
///         hook Vixen gained for this, and everything else in these options is what any
///         Vixen application anywhere writes. That is the point: Trinix is a target,
///         not a fork.
///     </para>
///     <para>
///         It prints rather than merely drawing, because the exit criterion has to be
///         checkable over a serial console by a script — the same argument
///         <c>trinix-wl-demo</c> and <c>trinix-vk-probe</c> are built on, one layer up.
///     </para>
/// </remarks>
static class Program {
    static int Main(string[] arguments) {
        var frames = 0;

        return UiApplication.Run(
            new UiApplicationOptions {
                Title = "Hello from Vixen",
                Organisation = "Trinix",
                Application = "HelloUi",
                Size = new Int2(640, 480),

                // The line this application exists to demonstrate. Without it Vixen
                // builds a DesktopPlatform, which is SDL, which Trinix does not ship.
                Platform = options => new TrinixPlatform(
                    new TrinixPlatformOptions {
                        Organisation = options.Organisation,
                        Application = options.Application,
                        ApplicationId = "io.trinix.helloui"
                    }
                ),

                Content = () => new Greeting(),

                Started = application => {
                    Console.WriteLine($"TRINIX-VIXEN: platform {application.Window.Id} on Trinix");
                    Console.WriteLine(
                        $"TRINIX-VIXEN: window {application.Window.ClientSize.X}x{application.Window.ClientSize.Y}"
                        + $" at {application.Window.DpiScale}x"
                        + $", framebuffer {application.Window.FramebufferSize.X}x{application.Window.FramebufferSize.Y}"
                    );
                },

                // Counted here rather than taken from --frames, so that the number
                // printed is frames that were actually drawn and presented.
                Frame = (_, _) => frames++,

                Stopping = _ => Console.WriteLine($"TRINIX-VIXEN-OK frames={frames}")
            },
            arguments
        );
    }
}

/// <summary>Something on the screen that is unmistakably text.</summary>
/// <remarks>
///     Built in code rather than in <c>.vxml</c>, and only because this is the first
///     one: markup brings the VXML compiler and the generated utility stylesheet, and
///     an application proving that a window works should fail for reasons about the
///     window. The shell and the real applications are markup.
/// </remarks>
sealed class Greeting : Component {
    /// <inheritdoc />
    protected override void Build(BuildContext ctx) {
        var root = ctx.Element(null, "div");
        ctx.Text(root, "Hello from Vixen, on Trinix.");
        ctx.Text(root, "Rendered by lavapipe, presented through wl_shm.");
    }
}
