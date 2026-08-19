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
            NumberType.Registration,
            nation,
            isExporter,
            orgId: 500027,
            MaterialType.Wood,
            glassRecyclingProcess: null,
            year: 2026
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
            NumberType.Accreditation,
            Nation.England,
            isExporter: false,
            orgId: 1,
            material,
            glassRecyclingProcess,
            year: 2026
        );

        number.Should().EndWith(expectedCode);
        number.Should().StartWith("A26ER");
    }

    [Fact]
    public async Task GenerateAsync_throws_and_does_not_touch_the_counter_when_glass_process_is_missing()
    {
        var (generator, store) = Create();

        var act = () =>
            generator.GenerateAsync(
                NumberType.Registration,
                Nation.England,
                isExporter: false,
                orgId: 1,
                MaterialType.Glass,
                glassRecyclingProcess: null,
                year: 2026
            );

        await act.Should().ThrowAsync<ArgumentException>();
        (await store.GetCurrentMaxAsync("R-ER")).Should().BeNull();
    }

    [Fact]
    public async Task GenerateAsync_zero_pads_OrgId_and_sequence()
    {
        var (generator, _) = Create();

        var number = await generator.GenerateAsync(
            NumberType.Registration,
            Nation.England,
            isExporter: false,
            orgId: 42,
            MaterialType.Wood,
            glassRecyclingProcess: null,
            year: 2026
        );

        number.Should().Be("R26ER0000420001WO");
    }

    [Fact]
    public async Task GenerateAsync_uses_the_last_two_digits_of_the_year()
    {
        var (generator, _) = Create();

        var number = await generator.GenerateAsync(
            NumberType.Registration,
            Nation.England,
            isExporter: false,
            orgId: 1,
            MaterialType.Wood,
            glassRecyclingProcess: null,
            year: 2031
        );

        number.Should().StartWith("R31");
    }

    [Fact]
    public async Task GenerateAsync_draws_independent_sequences_for_registration_and_accreditation_pools()
    {
        var (generator, store) = Create();

        await generator.GenerateAsync(
            NumberType.Registration,
            Nation.England,
            isExporter: false,
            orgId: 1,
            MaterialType.Wood,
            glassRecyclingProcess: null,
            year: 2026
        );
        await generator.GenerateAsync(
            NumberType.Registration,
            Nation.England,
            isExporter: false,
            orgId: 2,
            MaterialType.Wood,
            glassRecyclingProcess: null,
            year: 2026
        );
        var accreditationNumber = await generator.GenerateAsync(
            NumberType.Accreditation,
            Nation.England,
            isExporter: false,
            orgId: 3,
            MaterialType.Wood,
            glassRecyclingProcess: null,
            year: 2026
        );

        // Registration pool R-ER is already at 2; the accreditation pool A-ER is
        // untouched by those calls and starts fresh at 1 (AC2: separate counters).
        accreditationNumber.Should().Be("A26ER0000030001WO");
    }

    [Fact]
    public async Task GenerateAsync_never_returns_a_duplicate_sequence_under_concurrent_load()
    {
        var (generator, _) = Create();

        var tasks = Enumerable
            .Range(0, 50)
            .Select(i =>
                generator.GenerateAsync(
                    NumberType.Registration,
                    Nation.England,
                    isExporter: false,
                    orgId: i,
                    MaterialType.Wood,
                    glassRecyclingProcess: null,
                    year: 2026
                )
            );

        var numbers = await Task.WhenAll(tasks);

        var sequences = numbers.Select(n => n[11..15]).ToList();
        sequences.Should().OnlyHaveUniqueItems();
        sequences.Should().HaveCount(50);
    }

    [Fact]
    public async Task GenerateAsync_never_returns_a_duplicate_sequence_under_concurrent_load_for_accreditation()
    {
        // AC3 explicitly calls for this once for registration (above) and once for
        // accreditation - separate counters (AC2), so uniqueness must hold within
        // each type independently, not just across the combined set.
        var (generator, _) = Create();

        var tasks = Enumerable
            .Range(0, 50)
            .Select(i =>
                generator.GenerateAsync(
                    NumberType.Accreditation,
                    Nation.England,
                    isExporter: false,
                    orgId: i,
                    MaterialType.Wood,
                    glassRecyclingProcess: null,
                    year: 2026
                )
            );

        var numbers = await Task.WhenAll(tasks);

        var sequences = numbers.Select(n => n[11..15]).ToList();
        sequences.Should().OnlyHaveUniqueItems();
        sequences.Should().HaveCount(50);
        numbers.Should().OnlyContain(n => n.StartsWith("A26ER"));
    }
}
