using System.Runtime.CompilerServices;
using DpiShell.App;

namespace DpiShell.Tests;

static class NoRealDialogs
{
    [ModuleInitializer]
    internal static void Init() => UiGuard.Forbidden = true;
}
