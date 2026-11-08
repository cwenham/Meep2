namespace Meep2.Tests;

public class SetupTests
{
    [Test]
    public async Task Tests_run_on_dotnet_11()
    {
        await Assert.That(Environment.Version.Major).IsEqualTo(11);
    }
}
