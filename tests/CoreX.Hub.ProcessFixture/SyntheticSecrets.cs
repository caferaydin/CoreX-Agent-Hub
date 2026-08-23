namespace CoreX.Hub.ProcessFixture
{
    /// <summary>
    /// Synthetic credentials used only by the redaction tests. Nothing here is real, and
    /// nothing here is a credential for any system: the values exist so a test can prove
    /// they never reach a stored evidence blob.
    /// </summary>
    public static class SyntheticSecrets
    {
        public const string OpenAiToken = "sk-corexSYNTHETIC0000000000000000abcd";

        public const string GitHubToken = "ghp_corexSYNTHETIC0000000000000000abcd";

        public const string AwsAccessKey = "AKIACOREXSYNTHETIC00";

        public const string Jwt =
            "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJjb3JleC1zeW50aGV0aWMifQ.c29tZXN5bnRoZXRpY3NpZ25hdHVyZQ";

        public const string PasswordValue = "SyntheticCorexPassw0rd";

        public const string ConnectionString =
            "Server=localhost;Database=corex;User Id=corex;Password=" + PasswordValue + ";";

        public const string AuthorizationHeaderValue = "Bearer synthetic-corex-header-value";

        public const string PrivateKeyBody = "U1lOVEhFVElDLUNPUkVYLUtFWS1NQVRFUklBTA==";

        /// <summary>Text that only looks like a secret and must survive redaction unchanged.</summary>
        public static readonly string[] NearMisses =
        [
            "sketch-book was updated",
            "skip-this-step",
            "ghost_writer signed off",
            "AKIA is a prefix and AKIASHORT is not a key",
            "the user requested a password reset",
            "eyJ is not a token on its own",
            "Authorized: yes",
        ];

        public static string PrivateKeyBlock() =>
            "-----BEGIN RSA PRIVATE KEY-----\n"
            + PrivateKeyBody + "\n"
            + PrivateKeyBody + "\n"
            + "-----END RSA PRIVATE KEY-----";

        public static string[] AllSecretValues() =>
        [
            OpenAiToken,
            GitHubToken,
            AwsAccessKey,
            Jwt,
            PasswordValue,
            AuthorizationHeaderValue,
            PrivateKeyBody,
        ];

        public static string Payload()
        {
            var builder = new System.Text.StringBuilder();
            builder.AppendLine("openai=" + OpenAiToken);
            builder.AppendLine("github=" + GitHubToken);
            builder.AppendLine("aws=" + AwsAccessKey);
            builder.AppendLine("jwt=" + Jwt);
            builder.AppendLine("connection=" + ConnectionString);
            builder.AppendLine("Authorization: " + AuthorizationHeaderValue);
            builder.AppendLine("repeated=" + OpenAiToken);
            builder.AppendLine(PrivateKeyBlock());

            foreach (var line in NearMisses)
            {
                builder.AppendLine(line);
            }

            return builder.ToString();
        }
    }
}
