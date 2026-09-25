using Jellyfin.Plugin.StreamGenerator.Model;

namespace Jellyfin.Plugin.StreamGenerator.Tests;

public class StreamGeneratorPluginTests
{
    [Fact]
    public void PatchContextMenu_UsesCommandArgumentsInsteadOfMinifiedNames()
    {
        const string source = "function executeCommand(item,id,options){return new Promise(function(resolve,reject){switch(id){case\"copy-stream\":copy();break;}})}" +
                              "const commands=[{name:\"Download\",id:\"copy-stream\",icon:\"content_copy\"}];";

        var result = StreamGeneratorPlugin.PatchContextMenu(new PatchRequestPayload { Contents = source });

        result.Should().Contain("id:\"generate-stream\"");
        result.Should().Contain("case\"generate-stream\"");
        result.Should().Contain("window.streamGeneratorCommandContext={item:item,serverId:item.ServerId};");
        result.Should().NotContain("showStreamGeneratorPopup(c,u)");
        result.Should().Contain("window.showStreamGeneratorPopup(context.item.Id,context.serverId)");
        result.Should().Contain("import(window.ApiClient.getUrl('StreamGenerator/PopupContent.js'))");
        result.Should().NotContain("getResolveFunction");
    }

    [Fact]
    public void PatchContextMenu_WhenCopyStreamCommandIsMissing_ReturnsOriginalContent()
    {
        const string source = "const commands=[];";

        var result = StreamGeneratorPlugin.PatchContextMenu(new PatchRequestPayload { Contents = source });

        result.Should().Be(source);
    }

    [Fact]
    public void PatchContextMenu_WhenCommandHandlerCannotBeResolved_ReturnsOriginalContent()
    {
        const string source = "const commands=[{id:\"copy-stream\",icon:\"content_copy\"}];case\"copy-stream\":break;";

        var result = StreamGeneratorPlugin.PatchContextMenu(new PatchRequestPayload { Contents = source });

        result.Should().Be(source);
    }
}
