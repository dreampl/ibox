using FluentAssertions;
using JackiVpn.Application.Account;

namespace JackiVpn.UnitTests.Application;

public sealed class TelegramAvatarTests
{
    [Theory]
    [InlineData("<img class='tgme_page_photo_image' src='https://cdn4.cdn-telegram.org/file/photo.jpg'>")]
    [InlineData("<img src=\"https://cdn4.cdn-telegram.org/file/photo.jpg\" class=\"tgme_page_photo_image\">")]
    public void Reads_the_actual_profile_photo_regardless_of_attribute_order(string html) =>
        TelegramAvatar.ParsePublicPhoto(html)!.AbsoluteUri.Should().Be("https://cdn4.cdn-telegram.org/file/photo.jpg");

    [Theory]
    [InlineData("<meta property='og:image' content='https://telegram.org/img/t_logo.png'>")]
    [InlineData("<img class='tgme_page_photo_image' src='http://cdn4.cdn-telegram.org/file/photo.jpg'>")]
    [InlineData("<img class='tgme_page_photo_image' src='https://telegram.org.evil.example/photo.jpg'>")]
    [InlineData("<img class='tgme_page_photo_image' src='file:///private/photo.jpg'>")]
    [InlineData("<img class='other' src='https://cdn4.cdn-telegram.org/file/photo.jpg'>")]
    public void Does_not_import_generic_or_untrusted_images(string html) => TelegramAvatar.ParsePublicPhoto(html).Should().BeNull();

    [Fact]
    public void Decodes_html_entities_in_the_photo_url() =>
        TelegramAvatar.ParsePublicPhoto("<img class='tgme_page_photo_image' src='https://cdn4.telesco.pe/p.jpg?a=1&amp;b=2'>")!.Query.Should().Be("?a=1&b=2");
}
