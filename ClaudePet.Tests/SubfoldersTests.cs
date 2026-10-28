using System.IO;
using ClaudePet;

namespace ClaudePet.Tests;

public class SubfoldersTests
{
    [Fact]
    public void Lists_visible_subfolders_by_name_and_skips_hidden_ones()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pet-{Guid.NewGuid()}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "web"));
            Directory.CreateDirectory(Path.Combine(root, "Api"));
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            File.SetAttributes(Path.Combine(root, ".git"), FileAttributes.Directory | FileAttributes.Hidden);
            File.WriteAllText(Path.Combine(root, "readme.md"), "");

            Assert.Equal([Path.Combine(root, "Api"), Path.Combine(root, "web")], PetActions.Subfolders(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_folder_that_is_gone_has_no_subfolders() =>
        Assert.Empty(PetActions.Subfolders(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}")));
}
