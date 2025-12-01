namespace TaxAlpha.Core.Interfaces;

using TaxAlpha.Core.Models;

public interface ITransactionLoader
{
    List<RawTransaction> LoadAll(string folderPath);
}