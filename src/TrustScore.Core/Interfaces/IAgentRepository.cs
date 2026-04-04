namespace TrustScore.Core.Interfaces;

public interface IAgentRepository
{
    Task<double> GetTrustScoreAsync(string agentDid);
    Task UpsertTrustScoresAsync(Dictionary<string, double> scores);
}
