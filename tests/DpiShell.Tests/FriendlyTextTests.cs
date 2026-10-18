using DpiShell.App;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>Ошибки → текст для человека (Texts.Friendly).</summary>
public sealed class FriendlyTextTests
{
    [Fact]
    public void UnknownHelperCommand_SaysUpdateHelper()
    {
        // Старый помощник не знает команды окна — человеку не «неизвестная команда», а что делать.
        var text = Texts.Friendly(new HelperException("invalid", "Неизвестная команда «dns_set»."));
        Assert.Contains("обновите", text);
        Assert.DoesNotContain("dns_set", text);
    }

    [Fact]
    public void OtherInvalid_KeepsMessage() =>
        Assert.Equal("Нет поля cmd.", Texts.Friendly(new HelperException("invalid", "Нет поля cmd.")));
}
