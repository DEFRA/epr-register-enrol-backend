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
        BsonDifferences.Paths(Doc(left), Doc(right)).Should().BeEmpty();

    [Fact]
    public void Paths_ADifferentValue_NamesTheNestedField() =>
        BsonDifferences.Paths(Doc("{ a: { b: 1 } }"), Doc("{ a: { b: 2 } }")).Should().Equal("a.b");

    [Fact]
    public void Paths_AFieldOnOneSideOnly_IsADifference() =>
        BsonDifferences.Paths(Doc("{ a: 1 }"), Doc("{}")).Should().Equal("a");

    [Fact]
    public void Paths_ArrayElements_ShareOnePathSoEachFieldCountsOnce() =>
        BsonDifferences
            .Paths(Doc("{ a: [ { b: 1 }, { b: 2 } ] }"), Doc("{ a: [ { b: 9 }, { b: 8 } ] }"))
            .Should()
            .Equal("a[].b");

    [Fact]
    public void Paths_ArraysOfDifferentLength_AreOneDifference() =>
        BsonDifferences.Paths(Doc("{ a: [1, 2] }"), Doc("{ a: [1] }")).Should().Equal("a");
}
