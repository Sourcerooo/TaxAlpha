namespace TaxAlpha.Core.Interfaces
{
    public interface ITradingStrategy
    {
        string Name { get; }
        Task ExecuteAsync();
    }
}
