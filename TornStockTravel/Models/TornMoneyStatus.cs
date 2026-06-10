namespace TornStockTravel.Services;

public sealed record TornMoneyStatus(decimal Wallet, decimal Vault)
{
    public decimal Total => Wallet + Vault;

    public string WalletText => Wallet.ToString("N0");

    public string VaultText => Vault.ToString("N0");

    public string TotalText => Total.ToString("N0");
}
