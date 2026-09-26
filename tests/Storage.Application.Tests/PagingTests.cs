using Storage.Application.Abstractions;
using Storage.Application.Errors;

namespace Storage.Application.Tests;

public sealed class PagingTests
{
    [Fact]
    public void Leaving_the_page_out_reads_the_first_fifty()
    {
        var request = PageRequest.Of(null, null);

        Assert.Equal(1, request.Page);
        Assert.Equal(PageRequest.DefaultSize, request.PageSize);
        Assert.Equal(0, request.Skip);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(-1, 50)]
    [InlineData(1, 0)]
    [InlineData(1, PageRequest.MaxSize + 1)]
    [InlineData(int.MaxValue, PageRequest.MaxSize)]
    public void A_page_out_of_range_is_refused_rather_than_adjusted(int page, int pageSize)
    {
        var refusal = Assert.Throws<UseCaseException>(() => PageRequest.Of(page, pageSize));

        Assert.Equal(ErrorKind.Invalid, refusal.Kind);
        Assert.Equal(ErrorCodes.PageInvalid, refusal.Code);
    }

    [Fact]
    public void A_slice_knows_how_many_pages_there_are_in_all()
    {
        var page = Paged<int>.Slice(Enumerable.Range(1, 23).ToArray(), PageRequest.Of(3, 10));

        Assert.Equal([21, 22, 23], page.Items);
        Assert.Equal(23, page.Total);
        Assert.Equal(3, page.TotalPages);
    }

    [Fact]
    public void Past_the_last_page_is_empty_but_still_says_how_many_there_are()
    {
        var page = Paged<int>.Slice([1, 2, 3], PageRequest.Of(5, 10));

        Assert.Empty(page.Items);
        Assert.Equal(3, page.Total);
        Assert.Equal(1, page.TotalPages);
    }

    [Fact]
    public void Nothing_at_all_is_zero_pages()
    {
        Assert.Equal(0, Paged<int>.Empty(PageRequest.First()).TotalPages);
    }
}
