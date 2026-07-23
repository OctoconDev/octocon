namespace Interfold.DatabaseBootstrap;

/// <summary>Input contract for <see cref="ScyllaSeeder.BootstrapAsync"/>. The
/// built-in <c>cassandra / cassandra</c> credentials are hard-coded inside the seeder.</summary>
/// <param name="AppUser">Non-superuser role — <c>SUPERUSER = false, LOGIN = true</c>.</param>
/// <param name="AppPassword">Password for <see cref="AppUser"/>.</param>
/// <param name="AdminUser">Superuser role — <c>SUPERUSER = true, LOGIN = true</c>. Convention: <c>&lt;app&gt;_admin</c>.</param>
/// <param name="AdminPassword">Password for <see cref="AdminUser"/>.</param>
/// <param name="LockDefaultCassandra">When true (and neither user equals <c>cassandra</c>),
/// the seeder finishes by scrambling <c>cassandra</c>'s password and setting <c>LOGIN = false</c>.</param>
public sealed record ScyllaSeedOptions(
    string AppUser,
    string AppPassword,
    string AdminUser,
    string AdminPassword,
    bool LockDefaultCassandra);
