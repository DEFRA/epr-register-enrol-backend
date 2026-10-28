using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

public class RegulatoryNumberGeneratorTests
{
    private static (
        RegulatoryNumberGenerator Generator,
        FakeRegulatoryNumberSequenceCounterPersistence Store
    ) Create()
    {
        var store = new FakeRegulatoryNumberSequenceCounterPersistence();
        return (new RegulatoryNumberGenerator(store), store);
    }

    [Theory]
    [InlineData(Nation.England, false, "ER")]
    [InlineData(Nation.England, true, "EX")]
    [InlineData(Nation.Scotland, false, "SR")]
    [InlineData(Nation.Scotland, true, "SX")]
    [InlineData(Nation.Wales, false, "WR")]
    [InlineData(Nation.Wales, true, "WX")]
    [InlineData(Nation.NorthernIreland, false, "NR")]
    [InlineData(Nation.NorthernIreland, true, "NX")]
    public async Task GenerateAsync_produces_the_correct_AgencyType_for_every_nation_and_processing_type(
        Nation nation,
        bool isExporter,
        string expectedAgencyType
    )
    {
        var (generator, _) = Create();

        var number = await generator.GenerateAsync(
            new RegulatoryNumberSpec
            {
                Nation = nation,
                IsExporter = isExporter,
                OrgId = 500027,
                Material = MaterialType.Wood,
                GlassRecyclingProcess = null,
                Year = 2026,
            },
            TestContext.Current.CancellationToken
        );

        number.Should().MatchRegex($"^R26{expectedAgencyType}5000270001WO$");
    }

    [Theory]
    [InlineData(MaterialType.Steel, null, "ST")]
    [InlineData(MaterialType.Wood, null, "WO")]
    [InlineData(MaterialType.Aluminium, null, "AL")]
    [InlineData(MaterialType.Fibre, null, "FB")]
    [InlineData(MaterialType.Paper, null, "PA")]
    [InlineData(MaterialType.Plastic, null, "PL")]
    [InlineData(MaterialType.Glass, GlassRecyclingProcess.Remelt, "GR")]
    [InlineData(MaterialType.Glass, GlassRecyclingProcess.Other, "GO")]
    public async Task GenerateAsync_produces_the_correct_material_code(
        MaterialType material,
        GlassRecyclingProcess? glassRecyclingProcess,
        string expectedCode
    )
    {
        var (generator, _) = Create();

        var number = await generator.GenerateAsync(
            new RegulatoryNumberSpec
            {
                Nation = Nation.England,
                IsExporter = false,
                OrgId = 1,
                Material = material,
                GlassRecyclingProcess = glassRecyclingProcess,
                Year = 2026,
            },
            TestContext.Current.CancellationToken
        );

        number.Should().EndWith(expectedCode);
        number.Should().StartWith("R26ER");
    }

    [Fact]
    public async Task GenerateAsync_throws_and_does_not_touch_the_counter_when_glass_process_is_missing()
    {
        var (generator, store) = Create();

        var act = () =>
            generator.GenerateAsync(
                new RegulatoryNumberSpec
                {
                    Nation = Nation.England,
                    IsExporter = false,
                    OrgId = 1,
                    Material = MaterialType.Glass,
                    GlassRecyclingProcess = null,
                    Year = 2026,
                },
                TestContext.Current.CancellationToken
            );

        await act.Should().ThrowAsync<ArgumentException>();
        (await store.GetCurrentMaxAsync("R-ER", TestContext.Current.CancellationToken))
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task GenerateAsync_zero_pads_OrgId_and_sequence()
    {
        var (generator, _) = Create();

        var number = await generator.GenerateAsync(
            new RegulatoryNumberSpec
            {
                Nation = Nation.England,
                IsExporter = false,
                OrgId = 42,
                Material = MaterialType.Wood,
                GlassRecyclingProcess = null,
                Year = 2026,
            },
            TestContext.Current.CancellationToken
        );

        number.Should().Be("R26ER0000420001WO");
    }

    [Fact]
    public async Task GenerateAsync_uses_the_last_two_digits_of_the_year()
    {
        var (generator, _) = Create();

        var number = await generator.GenerateAsync(
            new RegulatoryNumberSpec
            {
                Nation = Nation.England,
                IsExporter = false,
                OrgId = 1,
                Material = MaterialType.Wood,
                GlassRecyclingProcess = null,
                Year = 2031,
            },
            TestContext.Current.CancellationToken
        );

        number.Should().StartWith("R31");
    }

    [Fact]
    public async Task GenerateAsync_never_returns_a_duplicate_sequence_under_concurrent_load()
    {
        var (generator, _) = Create();
        var ct = TestContext.Current.CancellationToken;

        var tasks = Enumerable
            .Range(0, 50)
            .Select(i =>
                generator.GenerateAsync(
                    new RegulatoryNumberSpec
                    {
                        Nation = Nation.England,
                        IsExporter = false,
                        OrgId = i,
                        Material = MaterialType.Wood,
                        GlassRecyclingProcess = null,
                        Year = 2026,
                    },
                    ct
                )
            );

        var numbers = await Task.WhenAll(tasks);

        var sequences = numbers.Select(n => n[11..15]).ToList();
        sequences.Should().OnlyHaveUniqueItems();
        sequences.Should().HaveCount(50);
    }

    [Theory]
    [InlineData(Nation.England, false, "R-ER")]
    [InlineData(Nation.England, true, "R-EX")]
    [InlineData(Nation.Scotland, false, "R-SR")]
    [InlineData(Nation.Scotland, true, "R-SX")]
    [InlineData(Nation.Wales, false, "R-WR")]
    [InlineData(Nation.Wales, true, "R-WX")]
    [InlineData(Nation.NorthernIreland, false, "R-NR")]
    [InlineData(Nation.NorthernIreland, true, "R-NX")]
    public async Task GenerateAsync_issues_a_registration_number_drawn_only_from_its_registration_pool(
        Nation nation,
        bool isExporter,
        string expectedPool
    )
    {
        // RA-690: the generator issues registration numbers only - accreditation references
        // are derived from the registration reference, so no A-* pool is ever drawn from.
        var (generator, store) = Create();

        var number = await generator.GenerateAsync(
            new RegulatoryNumberSpec
            {
                Nation = nation,
                IsExporter = isExporter,
                OrgId = 1,
                Material = MaterialType.Wood,
                GlassRecyclingProcess = null,
                Year = 2026,
            },
            TestContext.Current.CancellationToken
        );

        number.Should().StartWith("R");
        store.Keys.Should().Equal(expectedPool);
    }
}
