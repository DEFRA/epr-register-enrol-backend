using EprRegisterEnrolBackend.ReEx;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.ReEx;

public class ReExResultTests
{
    [Fact]
    public void Success_SetsExpectedProperties()
    {
        var result = ReExResult<string>.Success("value", 200);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("value");
        result.StatusCode.Should().Be(200);
        result.Error.Should().BeNull();
    }

    [Fact]
    public void Success_IsNotFound_IsClientError_IsUpstreamFailure_AreAllFalse()
    {
        // Covers the !IsSuccess short-circuit (false) branch on every derived flag.
        var result = ReExResult<string>.Success("value", 200);

        result.IsNotFound.Should().BeFalse();
        result.IsClientError.Should().BeFalse();
        result.IsUpstreamFailure.Should().BeFalse();
    }

    [Fact]
    public void Fail_SetsExpectedProperties()
    {
        var error = new ReExError(ReExErrorKind.ClientError, "bad request");

        var result = ReExResult<string>.Fail(error, 400);

        result.IsSuccess.Should().BeFalse();
        result.Value.Should().BeNull();
        result.StatusCode.Should().Be(400);
        result.Error.Should().BeSameAs(error);
    }

    [Fact]
    public void Fail_WithoutStatusCode_StatusCodeIsNull()
    {
        var result = ReExResult<string>.Fail(new ReExError(ReExErrorKind.TransportError));

        result.StatusCode.Should().BeNull();
    }

    [Fact]
    public void Fail_NotFound_IsNotFoundTrue_OthersFalse()
    {
        var result = ReExResult<string>.Fail(new ReExError(ReExErrorKind.NotFound));

        result.IsNotFound.Should().BeTrue();
        result.IsClientError.Should().BeFalse();
        result.IsUpstreamFailure.Should().BeFalse();
    }

    [Fact]
    public void Fail_ClientError_IsClientErrorTrue_OthersFalse()
    {
        var result = ReExResult<string>.Fail(new ReExError(ReExErrorKind.ClientError));

        result.IsNotFound.Should().BeFalse();
        result.IsClientError.Should().BeTrue();
        result.IsUpstreamFailure.Should().BeFalse();
    }

    [Theory]
    [InlineData(ReExErrorKind.ServerError)]
    [InlineData(ReExErrorKind.Timeout)]
    [InlineData(ReExErrorKind.TransportError)]
    [InlineData(ReExErrorKind.DeserializationError)]
    public void Fail_UpstreamFailureKinds_IsUpstreamFailureTrue(ReExErrorKind kind)
    {
        var result = ReExResult<string>.Fail(new ReExError(kind));

        result.IsUpstreamFailure.Should().BeTrue();
        result.IsNotFound.Should().BeFalse();
        result.IsClientError.Should().BeFalse();
    }

    [Fact]
    public void Fail_AuthError_IsUpstreamFailureFalse()
    {
        // AuthError matches none of the IsNotFound/IsClientError/IsUpstreamFailure kinds —
        // exercises the "falls through every pattern arm" (false) branch of the `is X or Y
        // or Z or W` pattern match, distinct from each individual matched kind above.
        var result = ReExResult<string>.Fail(new ReExError(ReExErrorKind.AuthError));

        result.IsNotFound.Should().BeFalse();
        result.IsClientError.Should().BeFalse();
        result.IsUpstreamFailure.Should().BeFalse();
    }

    [Fact]
    public void ReExError_StoresKindAndMessage()
    {
        var error = new ReExError(ReExErrorKind.ServerError, "boom");

        error.Kind.Should().Be(ReExErrorKind.ServerError);
        error.Message.Should().Be("boom");
    }

    [Fact]
    public void ReExError_DefaultMessage_IsNull()
    {
        var error = new ReExError(ReExErrorKind.ServerError);

        error.Message.Should().BeNull();
    }
}
