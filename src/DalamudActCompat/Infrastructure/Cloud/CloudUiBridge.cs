namespace DalamudActCompat.Infrastructure.Cloud;

internal sealed record CloudRegistrationRequest(
    string Username,
    string Password,
    string ActivationKey);

internal sealed record CloudLoginRequest(string Username, string Password);

internal sealed record CloudPasswordResetRequest(
    string Username,
    string ResetCode,
    string NewPassword,
    string RecoveryKey);

public sealed class CloudUiBridge
{
    internal CloudUiBridge(
        Func<CloudClientSnapshot> getSnapshot,
        Action<CloudRegistrationRequest> register,
        Action<CloudLoginRequest> login,
        Action<CloudPasswordResetRequest> resetPassword,
        Action logout,
        Action refresh,
        Action upload,
        Action<string> previewRestore,
        Action<string> restore,
        Action rollback)
    {
        GetSnapshot = getSnapshot;
        Register = register;
        Login = login;
        ResetPassword = resetPassword;
        Logout = logout;
        Refresh = refresh;
        Upload = upload;
        PreviewRestore = previewRestore;
        Restore = restore;
        Rollback = rollback;
    }

    internal Func<CloudClientSnapshot> GetSnapshot { get; }
    internal Action<CloudRegistrationRequest> Register { get; }
    internal Action<CloudLoginRequest> Login { get; }
    internal Action<CloudPasswordResetRequest> ResetPassword { get; }
    internal Action Logout { get; }
    internal Action Refresh { get; }
    internal Action Upload { get; }
    internal Action<string> PreviewRestore { get; }
    internal Action<string> Restore { get; }
    internal Action Rollback { get; }
}
