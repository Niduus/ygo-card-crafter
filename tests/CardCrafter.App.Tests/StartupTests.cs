using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CardCrafter.App.Services;
using CardCrafter.App.ViewModels;
using CardCrafter.App.Views;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;
using Microsoft.Data.Sqlite;

namespace CardCrafter.App.Tests;

public class StartupTests
{
    /// <summary>
    /// The app once closed right after opening: reading the game's card database hit a NULL column and the
    /// error escaped the startup code. Startup against an Omega install like that must keep running.
    /// </summary>
    [AvaloniaFact]
    public async Task StartsAgainstOmegaWithAnAwkwardDatabase()
    {
        using var tmp = new TempDirectory();
        var omega = Path.Combine(tmp.Path, "YGO Omega");
        var files = Path.Combine(omega, "YGO Omega_Data", "Files");
        Directory.CreateDirectory(Path.Combine(files, "Bundles"));
        Directory.CreateDirectory(Path.Combine(files, "Databases"));
        Directory.CreateDirectory(Path.Combine(files, "Scripts"));
        File.WriteAllText(Path.Combine(omega, "YGO Omega.exe"), "");
        File.WriteAllText(Path.Combine(files, "Scripts", "c1.lua"), "");
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(files, "Bundles", "db")};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE datas(id integer primary key, ot integer, alias integer, setcode integer, type integer,
                    atk integer, def integer, level integer, race integer, attribute integer, category integer, extra text);
                CREATE TABLE texts(id integer primary key, name text, desc text,
                    str1 text, str2 text, str3 text, str4 text, str5 text, str6 text, str7 text, str8 text,
                    str9 text, str10 text, str11 text, str12 text, str13 text, str14 text, str15 text, str16 text);
                INSERT INTO datas(id, type, race) VALUES(89631139, 17, 8192);
                INSERT INTO texts(id, name) VALUES(89631139, NULL);
                """;
            cmd.ExecuteNonQuery();
        }
        // A broken database next to the custom ones must not stop anything either.
        File.WriteAllText(Path.Combine(files, "Databases", "broken.db"), "SQLite format 3\0junk");

        var vm = new MainViewModel(new FakeDialogs(), new AppSettings { OmegaPath = omega });
        await vm.StartAsync([]);
        Assert.True(vm.OmegaFound);
        Assert.NotNull(vm.Catalog.Clash(89631139));
        Assert.Contains("passcodes in use", vm.ScanReport);
    }
}
