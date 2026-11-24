using Meep2.SDK.Security;

namespace Meep2.Tests;

public class PermissionDefinitionTests
{
    [Test]
    public async Task A_directive_reads_its_name_scope_authorities_and_requirements_and_writes_back_the_same()
    {
        var permission = Permission.ParseDirective(
            "#permission  WriteNotes:\"~/My Notes/**\" authority=home requires=ReadNotes authority=work requires=meep.files.write:~/notes/** // who may write");

        await Assert.That(permission.Name.ToString()).IsEqualTo("WriteNotes:~/My Notes/**");
        await Assert.That(permission.Authorities).IsEquivalentTo(["home", "work"]);
        await Assert.That(permission.Requires.Select(p => p.ToString())).IsEquivalentTo(["ReadNotes", "meep.files.write:~/notes/**"]);
        await Assert.That(permission.ToDirective())
            .IsEqualTo("#permission \"WriteNotes:~/My Notes/**\" authority=home authority=work requires=ReadNotes requires=meep.files.write:~/notes/**");

        var again = Permission.ParseDirective(permission.ToDirective());
        await Assert.That(again.Name).IsEqualTo(permission.Name);
        await Assert.That(again.Authorities).IsEquivalentTo(permission.Authorities);
        await Assert.That(again.Requires).IsEquivalentTo(permission.Requires);
        await Assert.That(Permission.ParseDirective("#permission ReadNotes").ToDirective()).IsEqualTo("#permission ReadNotes");
    }

    [Test]
    [Arguments("permission ReadNotes", "This isn't one")]
    [Arguments("#permission", "needs the permission's name")]
    [Arguments("#permission authority=home", "needs the permission's name")]
    [Arguments("#permission ReadNotes owner=me", "owner isn't something")]
    [Arguments("#permission ReadNotes authority=", "isn't something")]
    [Arguments("#permission ReadNotes authority=\"Home CA\"", "can't be an authority's name")]
    [Arguments("#permission ReadNotes requires=1bad", "can't be a permission's name")]
    [Arguments("#permission \"ReadNotes", "A quote isn't closed")]
    [Arguments("#permission Read:~/../etc", "could reach outside it")]
    public async Task A_directive_that_cant_be_read_is_refused_saying_why(string line, string why)
    {
        await Assert.That(() => Permission.ParseDirective(line)).Throws<FormatException>().WithMessageContaining(why);
    }

    [Test]
    public async Task A_permission_as_strict_as_its_definition_or_stricter_isnt_wider()
    {
        var definition = Permission.ParseDirective("#permission Write:~/notes/** authority=home authority=work requires=Read:~/notes/**");

        foreach (var narrower in new[]
        {
            "#permission Write:~/notes/** authority=home authority=work requires=Read:~/notes/**",
            "#permission Write:~/notes/drafts/** authority=home requires=Read:~/notes/** requires=Audit",
            "#permission Write:~/notes/a.md authority=work requires=Read",
        })
        {
            await Assert.That(Permission.ParseDirective(narrower).WiderThan(definition)).IsEmpty().Because(narrower);
        }
    }

    [Test]
    [Arguments("#permission Write authority=home requires=Read:~/notes/**", "reaches outside Write:~/notes/**")]
    [Arguments("#permission Write:~/notes/** requires=Read:~/notes/**", "may only be conferred by home, work, not by any authority")]
    [Arguments("#permission Write:~/notes/** authority=home authority=pals requires=Read:~/notes/**", "pals may not confer Write")]
    [Arguments("#permission Write:~/notes/** authority=home", "requires Read:~/notes/** as well")]
    [Arguments("#permission Write:~/notes/** authority=home requires=Read:~/notes/drafts/**", "requires Read:~/notes/** as well")]
    [Arguments("#permission Other authority=home requires=Read:~/notes/**", "isn't Write")]
    public async Task A_permission_looser_than_its_definition_is_wider_saying_how(string line, string why)
    {
        var definition = Permission.ParseDirective("#permission Write:~/notes/** authority=home authority=work requires=Read:~/notes/**");

        var failures = Permission.ParseDirective(line).WiderThan(definition);

        await Assert.That(failures.Select(f => f.Code)).Contains(VerificationFailureCode.WiderThanDefinition);
        await Assert.That(string.Join(" ", failures.Select(f => f.Reason))).Contains(why);
    }

    [Test]
    public async Task A_pipelines_directive_narrows_a_definition_keeping_what_it_doesnt_say()
    {
        var definition = Permission.ParseDirective("#permission Write authority=home authority=work requires=Read");

        var scoped = definition.NarrowedBy(Permission.ParseDirective("#permission Write:~/notes/** requires=Audit"));
        var oneAuthority = definition.NarrowedBy(Permission.ParseDirective("#permission Write authority=home"));
        var stranger = definition.NarrowedBy(Permission.ParseDirective("#permission Write authority=pals"));

        await Assert.That(scoped.ToDirective()).IsEqualTo("#permission Write:~/notes/** authority=home authority=work requires=Read requires=Audit");
        await Assert.That(scoped.WiderThan(definition)).IsEmpty();
        await Assert.That(oneAuthority.ToDirective()).IsEqualTo("#permission Write authority=home requires=Read");
        await Assert.That(stranger.WiderThan(definition).Single().Reason).Contains("pals may not confer Write");
    }

    [Test]
    public async Task Definitions_say_whether_a_permission_is_well_defined()
    {
        var definitions = PermissionDefinitions.Parse("""
            // Notes
            #permission Read
            #permission Write authority=home requires=Read

            #permission Audit authority=nobody requires=Missing
            """, ["home"]);

        await Assert.That(definitions.All.Select(p => p.Name.Name)).IsEquivalentTo(["Read", "Write", "Audit"]);
        await Assert.That(definitions.Find("Write")!.Requires.Single().Name).IsEqualTo("Read");
        await Assert.That(definitions.Find("Write:~/x")).IsNull();
        await Assert.That(definitions.IsWellDefined(Permission.ParseDirective("#permission Write:~/notes/** authority=home requires=Read"))).IsTrue();
        await Assert.That(definitions.Check(Permission.ParseDirective("#permission Delete")).Single().Code).IsEqualTo(VerificationFailureCode.UnknownPermission);
        await Assert.That(definitions.Check(Permission.ParseDirective("#permission Write requires=Read")).Single().Code).IsEqualTo(VerificationFailureCode.WiderThanDefinition);

        var audit = definitions.Check(Permission.ParseDirective("#permission Audit authority=nobody requires=Missing"));
        await Assert.That(audit.Select(f => f.Code).Distinct()).IsEquivalentTo([VerificationFailureCode.UndefinedRequirement]);
        await Assert.That(audit.Select(f => f.Reason)).IsEquivalentTo(["the authority nobody isn't in the trust store.", "it requires Missing, which isn't defined."]);
        await Assert.That(definitions.Problems).IsEquivalentTo(["Audit: the authority nobody isn't in the trust store.", "Audit: it requires Missing, which isn't defined."]);
    }

    [Test]
    public async Task A_permission_that_requires_itself_through_a_chain_isnt_well_defined()
    {
        var definitions = PermissionDefinitions.Parse("""
            #permission A requires=B
            #permission B requires=C:~/x/**
            #permission C requires=A
            #permission D requires=B
            """, []);

        await Assert.That(definitions.Problems).Contains("A: it requires itself, through A → B → C → A.");
        await Assert.That(definitions.Problems.Any(p => p.StartsWith("D:", StringComparison.Ordinal))).IsFalse();
        await Assert.That(definitions.Check(Permission.ParseDirective("#permission A requires=B")).Single().Code).IsEqualTo(VerificationFailureCode.UndefinedRequirement);
        // D doesn't require itself, so it's well defined, though B, which it requires, isn't.
        await Assert.That(definitions.IsWellDefined(Permission.ParseDirective("#permission D requires=B"))).IsTrue();
    }

    [Test]
    public async Task Definitions_that_cant_be_read_are_refused_naming_the_line()
    {
        await Assert.That(() => PermissionDefinitions.Parse("#permission Read\n\n#permission Write owner=me", []))
            .Throws<FormatException>().WithMessageContaining("Line 3: owner isn't something");
        await Assert.That(() => PermissionDefinitions.Parse("#permission Read\n#permission Read:~/x/**", []))
            .Throws<FormatException>().WithMessageContaining("Read is defined twice");
    }
}
