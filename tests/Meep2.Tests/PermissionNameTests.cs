using Meep2.SDK.Security;

namespace Meep2.Tests;

public class PermissionNameTests
{
    [Test]
    public async Task A_name_reads_with_and_without_a_scope_and_writes_back_the_same()
    {
        var plain = PermissionName.Parse("ReadNotes");
        var scoped = PermissionName.Parse("meep.files.write:~/notes/**");

        await Assert.That(plain.Name).IsEqualTo("ReadNotes");
        await Assert.That(plain.Scope).IsNull();
        await Assert.That(scoped.Name).IsEqualTo("meep.files.write");
        await Assert.That(scoped.Scope!.Pattern).IsEqualTo("~/notes/**");
        await Assert.That(scoped.ToString()).IsEqualTo("meep.files.write:~/notes/**");
        await Assert.That(PermissionName.Parse(scoped.ToString())).IsEqualTo(scoped);
    }

    [Test]
    public async Task Only_the_first_colon_separates_the_scope()
    {
        var permission = PermissionName.Parse("meep.network.send:https://example.com/**");

        await Assert.That(permission.Name).IsEqualTo("meep.network.send");
        await Assert.That(permission.Scope!.Pattern).IsEqualTo("https://example.com/**");
    }

    [Test]
    [Arguments("")]
    [Arguments("1abc")]
    [Arguments("a..b")]
    [Arguments(".a")]
    [Arguments("a.")]
    [Arguments("read notes")]
    [Arguments("read/notes")]
    [Arguments("name:")]
    [Arguments("name:/a/../b")]
    [Arguments("name:./a")]
    public async Task Names_and_scopes_that_cant_be_read_are_refused(string text)
    {
        await Assert.That(() => PermissionName.Parse(text)).Throws<FormatException>();
        await Assert.That(PermissionName.TryParse(text, out var permission)).IsFalse();
        await Assert.That(permission).IsNull();
    }

    [Test]
    public async Task Names_may_have_digits_dashes_and_underscores_after_a_letter()
    {
        await Assert.That(PermissionName.TryParse("meep.files-2.read_all", out var permission)).IsTrue();
        await Assert.That(permission!.Name).IsEqualTo("meep.files-2.read_all");
        await Assert.That(PermissionName.IsName("ReadNotes:~/x")).IsFalse();
    }

    [Test]
    public async Task A_permission_without_a_scope_covers_every_scope_but_not_the_other_way_round()
    {
        var all = PermissionName.Parse("meep.files.write");
        var notes = PermissionName.Parse("meep.files.write:/home/me/notes/**");

        await Assert.That(all.Covers(notes)).IsTrue();
        await Assert.That(all.Covers(all)).IsTrue();
        await Assert.That(notes.Covers(all)).IsFalse();
        await Assert.That(all.Covers(PermissionName.Parse("meep.files.read"))).IsFalse();
    }

    [Test]
    [Arguments("/a/**", "/a/**", true)]
    [Arguments("/a/**", "/a/b/**", true)]
    [Arguments("/a/**", "/a/b/*.txt", true)]
    [Arguments("/a/**", "/a/b.txt", true)]
    [Arguments("/a/**", "/a", true)]
    [Arguments("/a/**", "/ab/**", false)]
    [Arguments("/a/**", "/**", false)]
    [Arguments("/a/*.txt", "/a/x.txt", true)]
    [Arguments("/a/*.txt", "/a/x.md", false)]
    [Arguments("/a/*.txt", "/a/*", false)]
    [Arguments("/a/*/c/**", "/a/b/c/d", true)]
    [Arguments("/a/*/c/**", "/a/b/c/**", false)] // can't tell, so it says no
    [Arguments("**", "/anything/**", true)]
    public async Task A_scope_contains_a_narrower_one_and_says_no_when_it_cant_tell(string outer, string inner, bool expected)
    {
        await Assert.That(PermissionScope.Parse(outer).Contains(PermissionScope.Parse(inner))).IsEqualTo(expected);
    }

    [Test]
    [Arguments("/a/**", "/a", true)]
    [Arguments("/a/**", "/a/b", true)]
    [Arguments("/a/**", "/a/b/c.txt", true)]
    [Arguments("/a/**", "/ab", false)]
    [Arguments("/a/**", "/b/a", false)]
    [Arguments("/a/*.txt", "/a/notes.txt", true)]
    [Arguments("/a/*.txt", "/a/b/notes.txt", false)]
    [Arguments("/a/?.txt", "/a/x.txt", true)]
    [Arguments("/a/?.txt", "/a/xy.txt", false)]
    [Arguments("/a/**/c", "/a/c", true)]
    [Arguments("/a/**/c", "/a/b/b/c", true)]
    [Arguments("/a/**/c", "/a/b/c/d", false)]
    [Arguments("/a/**/b/**/c", "/a/x/b/y/b/z/c", true)]
    [Arguments("*.example.com", "api.example.com", true)]
    [Arguments("*.example.com", "example.com", false)]
    [Arguments("*.example.com", "a/b.example.com", false)]
    [Arguments("/A/**", "/a/b", false)]
    public async Task A_scope_matches_targets_part_by_part(string scope, string target, bool expected)
    {
        await Assert.That(PermissionScope.Parse(scope).Matches(target)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("/a/../etc/passwd")]
    [Arguments("/a/./b")]
    [Arguments("/a//b")]
    [Arguments("/a/b/")]
    [Arguments("")]
    public async Task Targets_that_could_reach_outside_a_scope_match_nothing(string target)
    {
        await Assert.That(PermissionScope.Parse("/a/**").Matches(target)).IsFalse();
        await Assert.That(PermissionScope.Parse("**").Matches(target)).IsFalse();
    }

    [Test]
    public async Task A_tilde_is_the_home_folder_in_the_scope_and_the_target()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var notes = PermissionScope.Parse("~/notes/**");

        await Assert.That(notes.Matches(Path.Combine(home, "notes", "today.md"))).IsTrue();
        await Assert.That(notes.Matches("~/notes/today.md")).IsTrue();
        await Assert.That(notes.Matches(Path.Combine(home, "other", "today.md"))).IsFalse();
        await Assert.That(notes.Matches("/elsewhere/~/notes/today.md")).IsFalse();
    }

    [Test]
    public async Task Allows_checks_a_name_and_a_target_together()
    {
        var notes = PermissionName.Parse("meep.files.write:/home/me/notes/**");
        var everything = PermissionName.Parse("meep.files.write");

        await Assert.That(notes.Allows("meep.files.write", "/home/me/notes/a.md")).IsTrue();
        await Assert.That(notes.Allows("meep.files.write", "/home/me/.ssh/id_ed25519")).IsFalse();
        await Assert.That(notes.Allows("meep.files.read", "/home/me/notes/a.md")).IsFalse();
        await Assert.That(everything.Allows("meep.files.write", "/anywhere")).IsTrue();
    }

    [Test]
    public async Task Many_wildcards_dont_take_exponential_time()
    {
        var scope = PermissionScope.Parse("/" + string.Join("/", Enumerable.Repeat("**", 20)) + "/x");
        var target = "/" + string.Join("/", Enumerable.Repeat("a", 200));
        var started = System.Diagnostics.Stopwatch.StartNew();

        await Assert.That(scope.Matches(target)).IsFalse();
        await Assert.That(started.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
    }
}
