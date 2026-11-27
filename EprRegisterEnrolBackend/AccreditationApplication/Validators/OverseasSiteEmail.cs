namespace EprRegisterEnrolBackend.AccreditationApplication.Validators;

// The overseas and interim site contact email pattern, shared by their validators so the copies can't drift.
// S6444: ContactEmail arrives straight off a client request body, so the match is given
// an explicit timeout rather than being left to run unbounded on the request thread.
// The trailing label also excludes '.' (unlike the two classes before it): with
// [^\s@]+\.[^\s@]+ every dot in the domain is a candidate split point for the literal,
// which makes a failing match quadratic in the input length. Pinning the final label to
// the last dot removes the ambiguity. Only behaviour change: a trailing-dot address
// ("a@b.com.") no longer validates, and that was never a valid address.
// A property rather than a static field: .editorconfig wants an internal static field named s_pattern, which reads
// badly from the validators that use it.
internal static class OverseasSiteEmail
{
    internal static System.Text.RegularExpressions.Regex Pattern { get; } = new(
        @"^[^\s@]+@[^\s@]+\.[^\s@.]+$",
        System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100)
    );
}
