using System.Text.Json;
using DpiShell.Core;
// Эталон сборки аргументов winws для сверки с роутерным сборщиком: пути заменены на {lists}/{fake}.
var cat = StrategyCatalog.Load(args[0]);
var paths = new AppPaths(root: "ROOT");
string Norm(string a) => a.Replace(@"ROOT\lists\", "{lists}/").Replace(@"ROOT\fake\", "{fake}/");
var outp = cat.Strategies.ToDictionary(s => s.Id, s => WinwsArgs.Build(s, paths).Select(Norm).ToList());
Console.WriteLine(JsonSerializer.Serialize(new { source = "WinwsArgs.Build", strategies = outp }, new JsonSerializerOptions { WriteIndented = true }));
