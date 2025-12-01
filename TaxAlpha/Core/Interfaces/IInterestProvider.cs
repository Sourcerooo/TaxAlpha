namespace TaxAlpha.Core.Interfaces;

public interface IInterestProvider
{
    decimal GetRate(int year);
}
