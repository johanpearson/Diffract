using Diffract.Fixtures;

namespace Diffract.Leftovers.Tests;

public class HiddenRowsTests : IDisposable
{
    private const string Repo = @"C:\src\shop";
    private readonly TempDirectory _temp = new();

    private HiddenRows Store => new(_temp.Combine("hidden"));

    private static Leftover Branch(string name, string commit = "1111111111111111111111111111111111111111", int risk = 2) =>
        new(LeftoverKind.LocalBranch, name, null, null, Verdict.NeedsReview, risk, "Not merged") { Commit = commit };

    private static List<string> Shown(SplitRows rows) => [.. rows.Shown.Select(l => l.Name)];

    [Fact]
    public void A_hidden_row_leaves_the_list_and_stays_hidden_after_a_restart()
    {
        var rows = new[] { Branch("a"), Branch("b") };
        Store.For(Repo).Hide(rows[0], rows);

        var afterRestart = Store.For(Repo).Split(rows);

        Assert.Equal(["b"], Shown(afterRestart));
        Assert.Equal(["a"], afterRestart.Hidden.Select(l => l.Name));
    }

    public static TheoryData<string, Leftover> Changes => new()
    {
        { "its branch moved", Branch("a", commit: "2222222222222222222222222222222222222222") },
        { "more commits are at risk", Branch("a", risk: 3) },
        { "its verdict changed", Branch("a") with { Verdict = Verdict.InUse } },
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public void A_hidden_row_comes_back_by_itself_when(string because, Leftover changed)
    {
        Store.For(Repo).Hide(Branch("a"), [Branch("a")]);

        Assert.True(Shown(Store.For(Repo).Split([changed])) is ["a"], because);
    }

    [Fact]
    public void Show_again_puts_it_back_in_the_list()
    {
        Store.For(Repo).Hide(Branch("a"), [Branch("a")]);

        Store.For(Repo).Unhide(Branch("a"), [Branch("a")]);

        Assert.Equal(["a"], Shown(Store.For(Repo).Split([Branch("a")])));
    }

    [Fact]
    public void A_row_that_changed_is_forgotten_so_it_does_not_hide_again_if_it_changes_back()
    {
        Store.For(Repo).Hide(Branch("a"), [Branch("a")]);
        var moved = Branch("a", commit: "2222222222222222222222222222222222222222");
        Store.For(Repo).Hide(Branch("b"), [moved, Branch("b")]);

        Assert.Equal(["a"], Shown(Store.For(Repo).Split([Branch("a"), Branch("b")])));
    }

    [Fact]
    public void Each_repo_has_its_own_hidden_rows()
    {
        Store.For(Repo).Hide(Branch("a"), [Branch("a")]);

        Assert.Equal(["a"], Shown(Store.For(@"C:\src\other").Split([Branch("a")])));
        Assert.Empty(Store.For(@"c:\SRC\Shop\").Split([Branch("a")]).Shown);
    }

    [Fact]
    public void A_file_that_cannot_be_read_only_shows_every_row_again()
    {
        Directory.CreateDirectory(Store.Folder);
        File.WriteAllText(Store.For(Repo).File, "{ not json");

        Assert.Equal(["a"], Shown(Store.For(Repo).Split([Branch("a")])));
    }

    [Fact]
    public void Hidden_rows_live_in_Diffracts_own_folder_never_in_the_repo()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(Path.Combine(local, "Diffract", "hidden"), HiddenRows.DefaultFolder);
    }

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }
}
