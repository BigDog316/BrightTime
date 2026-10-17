using BrightTime.Infrastructure.Security;
using Isopoh.Cryptography.Argon2;

namespace BrightTime.Infrastructure.Tests;

public sealed class Argon2PasswordHasherTests
{
    [Fact]
    public void A_hash_is_argon2id_with_the_current_parameters()
    {
        string hash = Argon2PasswordHasher.HashPassword("correct horse kettle staple");

        hash.ShouldStartWith($"$argon2id$v=19$m={Argon2PasswordHasher.MemoryKiB},t={Argon2PasswordHasher.Passes},p={Argon2PasswordHasher.Lanes}$");
    }

    [Fact]
    public void The_right_password_verifies_and_a_wrong_one_does_not()
    {
        string hash = Argon2PasswordHasher.HashPassword("correct horse kettle staple");

        Argon2PasswordHasher.Verify(hash, "correct horse kettle staple").ShouldBe(new PasswordCheck(true, false));
        Argon2PasswordHasher.Verify(hash, "correct horse kettle stapler").IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Two_hashes_of_one_password_differ_by_salt()
    {
        Argon2PasswordHasher.HashPassword("same password here").ShouldNotBe(Argon2PasswordHasher.HashPassword("same password here"));
    }

    [Fact]
    public void A_hash_made_with_weaker_parameters_verifies_and_asks_to_be_upgraded()
    {
        string old = Argon2.Hash("correct horse kettle staple", 1, 8_192, 1, Argon2Type.HybridAddressing, 32);

        Argon2PasswordHasher.Verify(old, "correct horse kettle staple").ShouldBe(new PasswordCheck(true, true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-phc-string")]
    public void No_hash_never_verifies(string? stored)
    {
        Argon2PasswordHasher.Verify(stored, "anything at all").IsValid.ShouldBeFalse();
    }
}
