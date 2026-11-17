using System;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class FomodImageTests
{
    [Fact]
    public void CreateBitmapSource_NullReturnsNull()
    {
        Assert.Null(FomodImageService.CreateBitmapSource(null));
    }

    [Fact]
    public void CreateBitmapSource_MismatchedBufferReturnsNull()
    {
        // A buffer whose length does not match width*height*4 is invalid and yields no image.
        var data = new FomodImageService.FomodImageData(new byte[8], 2, 2);
        Assert.Null(FomodImageService.CreateBitmapSource(data));
    }
}
