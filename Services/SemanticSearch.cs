using aida.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector;

namespace aida.Services;

public class SemanticSearch(
    IServiceScopeFactory scopeFactory,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator)
{
    private const int CandidateCount = 50;
    private const int RrfK = 60;

    private sealed class Hit
    {
        public Guid Id { get; set; }
        public string DocumentId { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string? Context { get; set; }
        public double Score { get; set; }
    }

    public async Task<IReadOnlyList<IngestedChunk>> SearchAsync(
        string text, string ownerId, IReadOnlyCollection<string>? documentIds, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(text))
            return [];

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var embedded = await embeddingGenerator.GenerateAsync([text]);
        var query = new Vector(embedded[0].Vector.ToArray());

        string[] ids = [];
        if (documentIds is not null)
        {
            ids = documentIds
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct()
                .ToArray();
        }

        var hits = await db.Database.SqlQuery<Hit>($"""
            WITH vec AS (
                SELECT c."Id",
                       ROW_NUMBER() OVER (ORDER BY c."Embedding" <=> {query}) AS rank
                FROM "Chunks" c
                WHERE c."OwnerId" = {ownerId}
                  AND (cardinality({ids}) = 0 OR c."DocumentId" = ANY({ids}))
                ORDER BY c."Embedding" <=> {query}
                LIMIT {CandidateCount}
            ),
            kw AS (
                SELECT c."Id",
                       ROW_NUMBER() OVER (
                           ORDER BY ts_rank_cd(to_tsvector('simple', c."Text"),
                                               websearch_to_tsquery('simple', {text})) DESC
                       ) AS rank
                FROM "Chunks" c
                WHERE c."OwnerId" = {ownerId}
                  AND (cardinality({ids}) = 0 OR c."DocumentId" = ANY({ids}))
                  AND to_tsvector('simple', c."Text") @@ websearch_to_tsquery('simple', {text})
                LIMIT {CandidateCount}
            ),
            fused AS (
                SELECT COALESCE(vec."Id", kw."Id") AS id,
                       COALESCE(1.0 / ({RrfK} + vec.rank), 0)
                     + COALESCE(1.0 / ({RrfK} + kw.rank), 0) AS score
                FROM vec
                FULL OUTER JOIN kw ON kw."Id" = vec."Id"
            )
            SELECT c."Id"         AS "Id",
                   c."DocumentId" AS "DocumentId",
                   c."Text"       AS "Text",
                   c."Context"    AS "Context",
                   f.score        AS "Score"
            FROM fused f
            JOIN "Chunks" c ON c."Id" = f.id
            ORDER BY f.score DESC
            LIMIT {maxResults}
            """).ToListAsync();

        return hits
            .Select(h => new IngestedChunk
            {
                DocumentId = h.DocumentId,
                Text = h.Text,
                Context = h.Context,
            })
            .ToList();
    }
}
