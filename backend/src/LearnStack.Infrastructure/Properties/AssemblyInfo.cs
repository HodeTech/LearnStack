using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("LearnStack.Tests.Unit")]
[assembly: InternalsVisibleTo("LearnStack.Tests.Integration")]
// Fatal proof runs only in its qualified Linux child, never in the test host.
[assembly: InternalsVisibleTo("LearnStack.Tests.JobLifecycleProbe")]

// Finite SMTP cleanup/crash proof owns retained work in its joined child.
[assembly: InternalsVisibleTo("LearnStack.Tests.InvitationSmtpProbe")]
