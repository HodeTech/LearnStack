using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class TenantBffSessionTests
{
    [Fact]
    public async Task Parent_EOF_during_actual_Next_prepare_joins_its_family_and_removes_the_private_tree()
    {
        await FrameworkOwnershipDriver.ProveParentEofAsync(RepositoryRoot());
    }

    [Fact]
    public async Task Parent_EOF_bridge_refuses_false_retirement_extra_output_and_retained_private_work()
    {
        foreach (var (mode, expected) in new[]
        {
            ("false-retirement", "Framework ownership fixture rejected its closed frame."),
            ("extra-output", "Framework ownership fixture emitted an unexpected extra frame."),
            ("invalid-utf8", "Framework ownership fixture output transport failed."),
            ("retained-root", "Framework ownership fixture retained its private tree."),
        })
        {
            var source = Directory.CreateTempSubdirectory("learnstack-framework-owner-counter-");
            var receipt = Path.Combine(source.FullName, "temporary-root.txt");
            try
            {
                var scripts = Path.Combine(source.FullName, "frontend/apps/web/scripts");
                Directory.CreateDirectory(scripts);
                await File.WriteAllTextAsync(Path.Combine(scripts, "verify-framework-runtime-ownership.mjs"), $$"""
                    import { mkdirSync, rmdirSync, writeFileSync } from 'node:fs';
                    import { join } from 'node:path';
                    const mode = {{JsonSerializer.Serialize(mode)}};
                    const receipt = {{JsonSerializer.Serialize(receipt)}};
                    let input = '';
                    let started = false;
                    const owned = join(process.env.TMPDIR, 'owned');
                    process.stdin.setEncoding('utf8');
                    process.stdin.on('data', (chunk) => {
                      input += chunk;
                      if (!input.includes('\n')) return;
                      if (started || input !== '{"protocol":1,"command":"start"}\n') process.exit(2);
                      started = true;
                      mkdirSync(owned);
                      writeFileSync(receipt, process.env.TMPDIR);
                      process.stdout.write('{"protocol":1,"phase":"prepare-held"}\n');
                    });
                    process.stdin.on('end', () => {
                      if (!started) process.exit(2);
                      if (mode === 'retained-root') writeFileSync(join(owned, 'retained'), 'x');
                      else rmdirSync(owned);
                      process.stdout.write(mode === 'false-retirement'
                        ? '{"protocol":1,"phase":"closed","ownerCode":1,"retired":false,"cleanup":true}\n'
                        : '{"protocol":1,"phase":"closed","ownerCode":1,"retired":true,"cleanup":true}\n');
                      if (mode === 'extra-output') process.stdout.write('{"protocol":1,"phase":"extra"}\n');
                      if (mode === 'invalid-utf8')
                        process.stdout.write(Buffer.concat([Buffer.from([0xff]), Buffer.from('PRIVATE-CANARY\n')]));
                      process.exitCode = 1;
                    });
                    process.stdin.resume();
                    """);
                var failure = await Record.ExceptionAsync(() => FrameworkOwnershipDriver.ProveParentEofAsync(source.FullName));
                var rejected = failure.Should().BeOfType<InvalidOperationException>().Which;
                rejected.Message.Should().Be(expected);
                rejected.InnerException.Should().BeNull("raw child decoder or path failures stay inside the closed bridge");
                rejected.ToString().Should().NotContain("PRIVATE-CANARY");
                var privateRoot = await File.ReadAllTextAsync(receipt);
                if (mode == "retained-root")
                {
                    Directory.Exists(privateRoot).Should().BeTrue("unknown retained work cannot gain cleanup credit");
                    File.Exists(Path.Combine(privateRoot, "owned/retained")).Should().BeTrue();
                    File.Delete(Path.Combine(privateRoot, "owned/retained"));
                    Directory.Delete(Path.Combine(privateRoot, "owned"));
                    Directory.Delete(privateRoot);
                }
                else Directory.Exists(privateRoot).Should().BeFalse();
            }
            finally { source.Delete(recursive: true); }
        }
    }
}
