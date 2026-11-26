using FluentAssertions;
using ReeveWorkflow.Core.Catalog;

namespace ReeveWorkflow.Core.Tests.Catalog;

public sealed class WorkflowPagingTests
{
    [Theory]
    [InlineData(int.MaxValue, int.MaxValue, 100, WorkflowPaging.MaxPage, 100)]
    [InlineData(0, 0, 100, 1, 1)]
    [InlineData(-7, -7, 100, 1, 1)]
    [InlineData(5, 25, 100, 5, 25)]
    [InlineData(5, 5000, 100, 5, 100)]
    public void ClampPage_bounds_page_and_pageSize(
        int page, int pageSize, int maxPageSize, int expectedPage, int expectedPageSize)
        => WorkflowPaging.ClampPage(page, pageSize, maxPageSize).Should().Be((expectedPage, expectedPageSize));

    [Theory]
    [InlineData(-5, -5, 200, 0, 1)]
    [InlineData(15, 5, 200, 15, 5)]
    [InlineData(int.MaxValue, int.MaxValue, 200, WorkflowPaging.MaxPage * 200, 200)]
    public void ClampSkipTake_bounds_skip_and_take(
        int skip, int take, int maxTake, int expectedSkip, int expectedTake)
        => WorkflowPaging.ClampSkipTake(skip, take, maxTake).Should().Be((expectedSkip, expectedTake));

    [Fact]
    public void A_hostile_page_keeps_its_offset_a_non_negative_Int32()
    {
        const int hostilePage = int.MaxValue;
        const int pageSize = 10;

        unchecked((hostilePage - 1) * pageSize).Should().BeNegative();

        var (page, size) = WorkflowPaging.ClampPage(hostilePage, pageSize, 100);
        ((long)(page - 1) * size).Should().BeInRange(0, int.MaxValue - 1);
    }

    [Fact]
    public void ApplyPage_windows_a_query_and_empties_an_overflowing_page()
    {
        var source = Enumerable.Range(0, 100).AsQueryable();

        WorkflowPaging.ApplyPage(source, 3, 5, 50).Should().Equal(Enumerable.Range(10, 5));
        WorkflowPaging.ApplyPage(source, int.MaxValue, 10, 50).Should().BeEmpty(
            "an absurd page is out of range, not a synonym for the first one");
    }

    [Fact]
    public void ApplySkipTake_windows_a_query_and_clamps_a_negative_offset()
    {
        var source = Enumerable.Range(0, 100).AsQueryable();

        WorkflowPaging.ApplySkipTake(source, 15, 5, 200).Should().Equal(Enumerable.Range(15, 5));
        WorkflowPaging.ApplySkipTake(source, -5, -5, 200).Should().Equal(Enumerable.Range(0, 1));
    }
}
