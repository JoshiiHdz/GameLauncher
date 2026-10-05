using System.Runtime.CompilerServices;
using GameLauncher;

namespace GameLauncher.Tests.TestSupport;

/// <summary>Runs once when the test assembly loads. A question the launcher asks with no window to ask it in would otherwise open a real
/// Windows message box and wait for someone to click it; here it is answered "No" instead.</summary>
internal static class NoRealDialogs
{
    [ModuleInitializer]
    internal static void Install() => AppShell.AnswerWithNoWindow = (_, _, _) => false;
}
