using System.Text;
using Diffract.Git.Changes;

namespace Diffract.Git.Tests.Changes;

/// <summary>Hunks from captured <c>git diff -U0</c> output. See Samples/capture-changes-samples.sh.</summary>
public class FilePatchTests
{
    private static FilePatch Read(string sample) => FilePatch.Parse(Sample.ReadBytes(sample));

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    [Fact]
    public void Changes_a_few_lines_apart_are_hunks_of_their_own()
    {
        var hunks = Read("diff-two-hunks.bin").Hunks;

        Assert.Equal([(2, 1, 2, 1), (6, 1, 6, 1)], hunks.Select(h => (h.OldStart, h.OldLines, h.NewStart, h.NewLines)));
        Assert.NotEqual(hunks[0].Id, hunks[1].Id);
    }

    [Fact]
    public void A_hunks_patch_is_its_files_header_and_that_hunk_alone()
    {
        var patch = Read("diff-two-hunks.bin");

        var second = Text(patch.PatchFor(patch.Hunks[1].Id)!);

        Assert.StartsWith("diff --git \"a/near \\303\\245\\303\\244\\303\\266.txt\"", second);
        Assert.Contains("+++ \"b/near \\303\\245\\303\\244\\303\\266.txt\"\t\n@@ -6 +6 @@\n-6\n+SIX\n", second);
        Assert.DoesNotContain("TWO", second);
    }

    [Fact]
    public void A_patch_keeps_every_byte_of_a_crlf_file()
    {
        var patch = Read("diff-crlf.bin");

        var bytes = patch.PatchFor(Assert.Single(patch.Hunks).Id)!;

        Assert.Equal(Sample.ReadBytes("diff-crlf.bin"), bytes);
        Assert.Contains("-two\r\n+TWO\r\n", Text(bytes));
    }

    [Fact]
    public void A_missing_final_newline_stays_in_the_hunk()
    {
        var patch = Read("diff-no-newline.bin");
        var hunk = Assert.Single(patch.Hunks);

        Assert.Equal((2, 1, 2, 2), (hunk.OldStart, hunk.OldLines, hunk.NewStart, hunk.NewLines));
        Assert.EndsWith("+after\n\\ No newline at end of file\n", Text(patch.PatchFor(hunk.Id)!));
    }

    [Fact]
    public void A_new_file_is_one_hunk_from_line_zero()
    {
        var hunk = Assert.Single(Read("diff-new-file.bin").Hunks);

        Assert.Equal((0, 0, 1, 2), (hunk.OldStart, hunk.OldLines, hunk.NewStart, hunk.NewLines));
    }

    [Fact]
    public void A_staged_rename_becomes_a_change_of_the_new_path_so_unstaging_a_hunk_keeps_the_rename()
    {
        var patch = Read("diff-staged-rename.bin");

        var text = Text(patch.PatchFor(Assert.Single(patch.Hunks).Id)!);

        Assert.Equal(
            "diff --git a/renamed.txt b/renamed.txt\nindex 4aa542e..94fba08 100644\n--- a/renamed.txt\n+++ b/renamed.txt\n@@ -2 +2 @@ keep\n-rename me\n+renamed\n",
            text);
    }

    [Fact]
    public void A_quoted_new_path_keeps_its_quoting()
    {
        var renamed = Text(Sample.ReadBytes("diff-two-hunks.bin"))
            .Replace("index f00c965", "similarity index 90%\nrename from old.txt\nrename to \"near \\303\\245\\303\\244\\303\\266.txt\"\nindex f00c965")
            .Replace("--- \"a/near \\303\\245\\303\\244\\303\\266.txt\"\t", "--- a/old.txt");
        var patch = FilePatch.Parse(Encoding.UTF8.GetBytes(renamed));

        var text = Text(patch.PatchFor(patch.Hunks[0].Id)!);

        Assert.StartsWith("diff --git \"a/near \\303\\245\\303\\244\\303\\266.txt\" \"b/near \\303\\245\\303\\244\\303\\266.txt\"\nindex", text);
        Assert.Contains("--- \"a/near \\303\\245\\303\\244\\303\\266.txt\"\t\n+++ \"b/near", text);
        Assert.DoesNotContain("rename", text);
    }

    [Fact]
    public void A_binary_file_has_no_hunks()
    {
        Assert.Empty(Read("diff-binary.bin").Hunks);
    }

    [Fact]
    public void No_diff_has_no_hunks_and_an_unknown_id_has_no_patch()
    {
        var patch = FilePatch.Parse([]);

        Assert.Empty(patch.Hunks);
        Assert.Null(patch.PatchFor("0123"));
    }

    [Fact]
    public void A_hunk_with_the_same_lines_in_another_version_of_the_file_gets_another_id()
    {
        var before = Read("diff-two-hunks.bin");
        var changed = Sample.ReadBytes("diff-two-hunks.bin");
        var otherIndexLine = Encoding.UTF8.GetBytes(Text(changed).Replace("index f00c965..f59ce60", "index f00c965..0000001"));

        var after = FilePatch.Parse(otherIndexLine);

        Assert.NotEqual(before.Hunks[1].Id, after.Hunks[1].Id);
    }

    [Fact]
    public void Two_files_in_one_diff_each_keep_their_own_header()
    {
        var both = Sample.ReadBytes("diff-crlf.bin").Concat(Sample.ReadBytes("diff-no-newline.bin")).ToArray();

        var patch = FilePatch.Parse(both);

        Assert.Equal(2, patch.Hunks.Count);
        Assert.Equal(Sample.ReadBytes("diff-no-newline.bin"), patch.PatchFor(patch.Hunks[1].Id));
    }

    [Fact]
    public void The_samples_are_captured_with_the_arguments_the_reader_uses()
    {
        var script = Sample.Script("capture-changes-samples.sh");
        var options = FilePatch.Arguments(new DiffTarget("x", null, Staged: false)).Skip(1).TakeWhile(a => a != "--");

        Assert.Contains($"diff_args='{string.Join(' ', options)}'", script);
        Assert.Equal(["--cached", "-M", "--", ":(literal)new", ":(literal)old"], FilePatch.Arguments(new DiffTarget("new", "old", Staged: true)).Skip(7));
    }
}
