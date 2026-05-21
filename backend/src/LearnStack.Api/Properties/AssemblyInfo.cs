using System.Runtime.CompilerServices;

// LearnStackExceptionHandler.ShouldCapture is internal so tests can hold
// it to the Sentry-vs-OTel boundary contract without exposing the rule
// to module code.
[assembly: InternalsVisibleTo("LearnStack.Tests.Unit")]
[assembly: InternalsVisibleTo("LearnStack.Tests.Architecture")]
