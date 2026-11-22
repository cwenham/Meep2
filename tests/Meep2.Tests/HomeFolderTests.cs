using Meep2.Shared;

namespace Meep2.Tests;

public class HomeFolderTests
{
    [Test]
    public async Task A_tilde_at_the_start_is_the_home_folder_and_nowhere_else()
    {
        var home = HomeFolder.Path;
        await Assert.That(HomeFolder.Expand("~")).IsEqualTo(home);
        await Assert.That(HomeFolder.Expand("~/.ssh/id_ed25519")).IsEqualTo(Path.Join(home, ".ssh/id_ed25519"));
        await Assert.That(HomeFolder.Expand("~\\notes.txt")).IsEqualTo(Path.Join(home, "notes.txt"));
        await Assert.That(HomeFolder.Expand("~someone/file")).IsEqualTo("~someone/file");
        await Assert.That(HomeFolder.Expand("/var/~/file")).IsEqualTo("/var/~/file");
        await Assert.That(HomeFolder.Expand("relative/file")).IsEqualTo("relative/file");
    }
}
