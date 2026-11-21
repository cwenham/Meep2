using Meep2.Daemon.Subscriptions;

namespace Meep2.Tests;

[NotInParallel("client-names")]
public class ClientNamesTests
{
    [Test]
    public async Task A_full_table_of_client_names_drops_those_kept_too_long_to_make_room()
    {
        var clock = 10_000_000L;
        ClientNames.Clear();
        ClientNames.Now = () => clock;
        try
        {
            for (var i = 0; i < 4096; i++)
                ClientNames.Remember($"10.0.{i / 256}.{i % 256}", "old.example", at: 0); // looked up long ago
            ClientNames.NameOf("192.0.2.1");

            await Assert.That(ClientNames.Knows("192.0.2.1")).IsTrue();
            await Assert.That(ClientNames.Count).IsLessThan(100);

            ClientNames.Clear();
            for (var i = 0; i < 4096; i++)
                ClientNames.Remember($"10.0.{i / 256}.{i % 256}", "new.example", at: clock); // all fresh
            ClientNames.NameOf("192.0.2.2");

            await Assert.That(ClientNames.Knows("192.0.2.2")).IsFalse(); // nothing old enough to drop, so it waits
        }
        finally
        {
            ClientNames.Now = () => Environment.TickCount64;
            ClientNames.Clear();
        }
    }
}
