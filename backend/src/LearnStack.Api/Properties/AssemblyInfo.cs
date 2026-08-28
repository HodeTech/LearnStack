using System.Runtime.CompilerServices;

// LearnStackExceptionHandler.ShouldCapture is internal so tests can hold
// it to the Sentry-vs-OTel boundary contract without exposing the rule
// to module code.
[assembly: InternalsVisibleTo("LearnStack.Tests.Unit")]
[assembly: InternalsVisibleTo("LearnStack.Tests.Architecture")]

// The composition root's credential guard is asserted for what it refuses in the
// unit suite, and for what the SERVER refuses — a runtime role granted BYPASSRLS,
// which no connection string can reveal — against a real cluster here.
[assembly: InternalsVisibleTo("LearnStack.Tests.Integration")]
