using LearnStack.SharedKernel;
using LearnStack.SharedKernel.Identifiers;
using Vogen;

namespace LearnStack.Modules.Education.Domain;

[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct CourseId : IStronglyTypedId<Guid>;

[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct LessonId : IStronglyTypedId<Guid>;
