using System.Runtime.CompilerServices;

// The architecture tests assert that the "one active execution per task" index filter
// agrees with the domain state classification. That check needs the internal model
// constant; nothing else is exposed.
[assembly: InternalsVisibleTo("CoreX.Hub.ArchitectureTests")]
