using EprRegisterEnrolBackend.DomainModel.Reconciliation;
using FluentAssertions;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.DomainModel.Reconciliation;

public class BsonDifferencesTests
{
    private static BsonDocument Doc(string json) => BsonDocument.Parse(json);

    [Theory]
    [InlineData("{}", "{ a: null }")]
    [InlineData("{}", "{ a: '' }")]
    [InlineData("{}", "{ a: [] }")]
    [InlineData("{ a: null }", "{ a: [] }")]
    public void Paths_EmptyValuesOfAnyKind_AreTheSame(string left, string right) =>
        BsonDifferences.Compare(Doc(left), Doc(right)).Paths.Should().BeEmpty();

    [Fact]
    public void Paths_ADifferentValue_NamesTheNestedField() =>
        BsonDifferences
            .Compare(Doc("{ a: { b: 1 } }"), Doc("{ a: { b: 2 } }"))
            .Paths.Should()
            .Equal("a.b");

    [Fact]
    public void Paths_AFieldOnOneSideOnly_IsADifference() =>
        BsonDifferences.Compare(Doc("{ a: 1 }"), Doc("{}")).Paths.Should().Equal("a");

    [Fact]
    public void Paths_ArrayElements_ShareOnePathSoEachFieldCountsOnce() =>
        BsonDifferences
            .Compare(Doc("{ a: [ { b: 1 }, { b: 2 } ] }"), Doc("{ a: [ { b: 9 }, { b: 8 } ] }"))
            .Paths.Should()
            .Equal("a[].b");

    [Fact]
    public void Paths_ArraysOfDifferentLength_AreOneDifference() =>
        BsonDifferences.Compare(Doc("{ a: [1, 2] }"), Doc("{ a: [1] }")).Paths.Should().Equal("a");

    [Theory]
    [InlineData("{ a: [] }", "{ a: [1] }")]
    [InlineData("{ a: [''] }", "{ a: [] }")]
    [InlineData("{ a: ['', 'x'] }", "{ a: ['x'] }")]
    public void Paths_ListsOfDifferentLength_DifferEvenWhenOneIsBlank(string left, string right) =>
        BsonDifferences.Compare(Doc(left), Doc(right)).Paths.Should().Equal("a");

    [Theory]
    [InlineData("{}", "{ a: 'x' }")]
    [InlineData("{ a: null }", "{ a: 'x' }")]
    [InlineData("{ a: '' }", "{ a: 'x' }")]
    [InlineData("{ a: [] }", "{ a: ['x'] }")]
    public void ModelOnly_LegacyBlankAndTheModelHoldingAValue_IsFlagged(
        string legacy,
        string model
    ) => BsonDifferences.Compare(Doc(legacy), Doc(model)).ModelOnly.Should().Equal("a");

    [Theory]
    [InlineData("{ a: 'x' }", "{}")]
    [InlineData("{ a: 'x' }", "{ a: 'y' }")]
    public void ModelOnly_LegacyHoldingAValue_IsNotFlagged(string legacy, string model) =>
        BsonDifferences.Compare(Doc(legacy), Doc(model)).ModelOnly.Should().BeEmpty();
}
