using FluentAssertions;
using ReeveWorkflow.Core.Expressions;

namespace ReeveWorkflow.Core.Tests.Expressions;

public sealed class FuzzTests
{
    private const int FuzzCount = 10000;
    private const int RandomSeed = 42;

    [Fact]
    public void Fuzz_Parser_Handles_AllRandomInputs_Without_Crashing()
    {
        var random = new System.Random(RandomSeed);
        int validParses = 0;
        int syntaxErrors = 0;
        var exceptions = new Dictionary<string, int>();

        for (int i = 0; i < FuzzCount; i++)
        {
            int length = random.Next(1, 200);
            var input = new string(Enumerable.Range(0, length)
                .Select(_ => (char)random.Next(32, 127))
                .ToArray());

            try
            {
                var ast = ExpressionParser.Parse(input);
                validParses++;
            }
            catch (ExpressionSyntaxException)
            {
                syntaxErrors++;
            }
            catch (Exception ex)
            {
                var typeName = ex.GetType().Name;
                if (!exceptions.ContainsKey(typeName))
                    exceptions[typeName] = 0;
                exceptions[typeName]++;

                if (!(ex is ExpressionSyntaxException))
                {
                    throw new InvalidOperationException(
                        $"Unexpected exception type {typeName} at iteration {i}: {ex.Message}", ex);
                }
            }
        }

        exceptions.Should().BeEmpty("No unexpected exception types should occur");
        (validParses + syntaxErrors).Should().Be(FuzzCount);
    }

    [Fact]
    public void Fuzz_Parser_Never_Throws_NullReferenceException()
    {
        var random = new System.Random(RandomSeed);

        for (int i = 0; i < FuzzCount; i++)
        {
            int length = random.Next(1, 200);
            var input = new string(Enumerable.Range(0, length)
                .Select(_ => (char)random.Next(32, 127))
                .ToArray());

            try
            {
                ExpressionParser.Parse(input);
            }
            catch (NullReferenceException)
            {
                throw new InvalidOperationException(
                    $"NullReferenceException at iteration {i} with input: {input}");
            }
            catch (ExpressionSyntaxException)
            {
            }
            catch
            {
                throw;
            }
        }
    }

    [Fact]
    public void Fuzz_Parser_Never_Throws_StackOverflowException()
    {
        var random = new System.Random(RandomSeed);

        for (int i = 0; i < FuzzCount; i++)
        {
            int length = random.Next(1, 200);
            var input = new string(Enumerable.Range(0, length)
                .Select(_ => (char)random.Next(32, 127))
                .ToArray());

            try
            {
                ExpressionParser.Parse(input);
            }
            catch (StackOverflowException)
            {
                throw new InvalidOperationException(
                    $"StackOverflowException at iteration {i} with input: {input}");
            }
            catch (ExpressionSyntaxException)
            {
            }
            catch
            {
                throw;
            }
        }
    }
}
