using CardCrafter.Core.Database;
using CardCrafter.Core.Model;
using CardCrafter.Core.Omega;

namespace CardCrafter.Core.Tests;

public class OmegaTests
{
    private static string FakeOmega(string root, bool looseFiles)
    {
        var omega = Path.Combine(root, "YGO Omega");
        Directory.CreateDirectory(Path.Combine(omega, "YGO Omega_Data"));
        File.WriteAllText(Path.Combine(omega, "YGO Omega.exe"), "");
        if (looseFiles)
        {
            var script = Path.Combine(omega, "Files", "Scripts");
            Directory.CreateDirectory(script);
            File.WriteAllText(Path.Combine(script, "constant.lua"), "");
            for (int i = 0; i < 5; i++) File.WriteAllText(Path.Combine(script, $"c{1000 + i}.lua"), "");
            var pics = Path.Combine(omega, "Files", "Pics");
            Directory.CreateDirectory(pics);
            for (int i = 0; i < 25; i++) File.WriteAllText(Path.Combine(pics, $"{1000 + i}.jpg"), "");
            CdbFile.WriteFresh(Path.Combine(omega, "Files", "Databases", "cards.cdb"), [CdbEncoder.Encode(new CardData { Id = 1000 }, [], 3)]);
        }
        return omega;
    }

    [Fact]
    public void Locator_FindsInstallByContents()
    {
        using var tmp = new TempDir();
        var omega = FakeOmega(tmp.Path, looseFiles: false);
        Assert.True(OmegaLocator.LooksLikeOmega(omega));
        Assert.False(OmegaLocator.LooksLikeOmega(tmp.Path));
        var found = OmegaLocator.Find(omega, scanDrives: false);
        Assert.NotNull(found);
        Assert.Equal(Path.GetFullPath(omega), found.Root);
    }

    [Fact]
    public void Inventory_FindsLooseFileLayout()
    {
        using var tmp = new TempDir();
        var omega = FakeOmega(tmp.Path, looseFiles: true);
        var inv = OmegaInventory.Scan(omega);
        Assert.True(inv.SupportsLooseFiles);
        Assert.EndsWith(Path.Combine("Files", "Scripts"), inv.BestScriptFolder);
        Assert.EndsWith(Path.Combine("Files", "Databases"), inv.BestDatabaseFolder);
        Assert.EndsWith(Path.Combine("Files", "Pics"), inv.BestImageFolder);
        Assert.Contains("Card Crafter can install", inv.Report());
    }

    /// <summary>The layout found on a real YGO Omega install (Files\Bundles\db is the official database).</summary>
    private static string RealOmegaLayout(string root)
    {
        var omega = Path.Combine(root, "YGO Omega");
        var files = Path.Combine(omega, "YGO Omega_Data", "Files");
        Directory.CreateDirectory(files);
        File.WriteAllText(Path.Combine(omega, "YGO Omega.exe"), "");
        var official = Enumerable.Range(1, 5001).Select(i => CdbEncoder.Encode(new CardData { Id = i }, [], 3));
        Directory.CreateDirectory(Path.Combine(files, "Bundles"));
        CdbFile.WriteFresh(Path.Combine(files, "Bundles", "db"), official);
        File.Move(Path.Combine(files, "Bundles", "db"), Path.Combine(files, "Bundles", "db.tmp"));
        File.Move(Path.Combine(files, "Bundles", "db.tmp"), Path.Combine(files, "Bundles", "db"));
        CdbFile.WriteFresh(Path.Combine(files, "Databases", "Infestation.db"), [CdbEncoder.Encode(new CardData { Id = 200000001 }, [], 3)]);
        CdbFile.WriteFresh(Path.Combine(files, "Databases", "Elder Divine.cdb"), [CdbEncoder.Encode(new CardData { Id = 200000002 }, [], 3)]);
        CdbFile.WriteFresh(Path.Combine(files, "Databases", "Kitharas.db"), [CdbEncoder.Encode(new CardData { Id = 200000003 }, [], 3)]);
        Directory.CreateDirectory(Path.Combine(files, "Scripts"));
        File.WriteAllText(Path.Combine(files, "Scripts", "c200000001.lua"), "");
        Directory.CreateDirectory(Path.Combine(files, "Arts"));
        for (int i = 0; i < 25; i++) File.WriteAllText(Path.Combine(files, "Arts", $"{200000000 + i}.jpg"), "");
        return omega;
    }

    [Fact]
    public void RealLayout_InstallsIntoDatabasesFolderAsDb()
    {
        using var tmp = new TempDir();
        var omega = RealOmegaLayout(tmp.Path);
        var inv = OmegaInventory.Scan(omega);
        var files = Path.Combine(omega, "YGO Omega_Data", "Files");
        Assert.Equal(Path.Combine(files, "Bundles", "db"), inv.OfficialDatabase!.Path);
        Assert.Equal(Path.Combine(files, "Databases"), inv.BestDatabaseFolder);
        Assert.Equal(".db", inv.DatabaseExtension);
        Assert.Equal(Path.Combine(files, "Scripts"), inv.BestScriptFolder);
        Assert.Equal(Path.Combine(files, "Arts"), inv.BestImageFolder);

        var export = PackExporter.Export(Samples.Pack(Samples.DragonCommander()), Path.Combine(tmp.Path, "out"));
        var result = OmegaInstaller.Install(export, inv, Path.Combine(tmp.Path, "b"));
        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(files, "Databases", "Test Pack.db")));
        Assert.True(File.Exists(Path.Combine(files, "Scripts", "c100000002.lua")));
        Assert.Equal(5001, CdbFile.Read(inv.OfficialDatabase.Path).Count);
        Assert.Contains("Databases as <pack name>.db", inv.Report());
    }

    [Fact]
    public void RealLayout_RefusesPasscodesUsedByOtherCards()
    {
        using var tmp = new TempDir();
        var omega = RealOmegaLayout(tmp.Path);
        var clash = Samples.DragonCommander();
        clash.Card.Id = 200000001; // already used by Infestation.db
        var official = Samples.PotOfGreedClone();
        official.Card.Id = 42; // an official card
        var export = PackExporter.Export(Samples.Pack(clash, official), Path.Combine(tmp.Path, "out"));
        var result = OmegaInstaller.Install(export, OmegaInventory.Scan(omega), Path.Combine(tmp.Path, "b"));
        Assert.False(result.Success);
        Assert.Equal(2, result.Clashes.Count);
        Assert.Contains(result.Clashes, c => c.Contains("Infestation.db"));
        Assert.Contains(result.Clashes, c => c.Contains(Path.Combine("Bundles", "db")));
    }

    [Fact]
    public void Inventory_ReportsPackedInstall()
    {
        using var tmp = new TempDir();
        var inv = OmegaInventory.Scan(FakeOmega(tmp.Path, looseFiles: false));
        Assert.False(inv.SupportsLooseFiles);
        Assert.Contains("export to a folder", inv.Report());
    }

    [Fact]
    public void Export_WritesStandardLayout_AndSkipsBrokenCards()
    {
        using var tmp = new TempDir();
        var broken = Samples.PotOfGreedClone();
        broken.Card.Id = 100000099;
        broken.Effects[0].When = null;
        var pack = Samples.Pack(Samples.DragonCommander(), broken);

        var result = PackExporter.Export(pack, Path.Combine(tmp.Path, "out"));
        Assert.Equal(1, result.Written);
        Assert.True(File.Exists(Path.Combine(tmp.Path, "out", "script", "c100000002.lua")));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "out", "script", "c100000099.lua")));
        // The database still lists both cards so text can be edited; only the script is withheld.
        Assert.Equal(2, CdbFile.Read(result.DatabasePath).Count);
    }

    [Fact]
    public void Install_CopiesAndBacksUp()
    {
        using var tmp = new TempDir();
        var omega = FakeOmega(tmp.Path, looseFiles: true);
        var pack = Samples.Pack(Samples.DragonCommander());
        var export = PackExporter.Export(pack, Path.Combine(tmp.Path, "out"));
        var inv = OmegaInventory.Scan(omega);
        var backups = Path.Combine(tmp.Path, "backups");

        var first = OmegaInstaller.Install(export, inv, backups);
        Assert.True(first.Success, first.Error);
        Assert.True(File.Exists(Path.Combine(omega, "Files", "Scripts", "c100000002.lua")));
        Assert.True(File.Exists(Path.Combine(omega, "Files", "Databases", "Test Pack.cdb")));
        Assert.Empty(first.BackedUp);

        var second = OmegaInstaller.Install(export, inv, backups);
        Assert.True(second.Success, second.Error);
        Assert.Equal(2, second.BackedUp.Count);
    }

    [Fact]
    public void Install_RefusesToReplaceSomeoneElsesDatabase()
    {
        using var tmp = new TempDir();
        var omega = FakeOmega(tmp.Path, looseFiles: true);
        var pack = Samples.Pack(Samples.DragonCommander());
        pack.Name = "cards";
        var export = PackExporter.Export(pack, Path.Combine(tmp.Path, "out"));
        var result = OmegaInstaller.Install(export, OmegaInventory.Scan(omega), Path.Combine(tmp.Path, "b"));
        Assert.False(result.Success);
        Assert.Contains("not made by Card Crafter", result.Error);
    }
}
