using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Wildpinkler.App.Models.Fomod;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Covers the additive info.xml metadata path of the FOMOD parser: identity fields are filled from
/// info.xml, and a FOMOD without info.xml parses exactly as before.
/// </summary>
public sealed class FomodInstallerParserTests
{
    private const string MinimalConfig = """
        <config>
          <moduleName>Test Mod</moduleName>
        </config>
        """;

    private const string InfoXml = """
        <fomod>
          <Name>Test Mod</Name>
          <Author>Some Author</Author>
          <Version>1.2.3</Version>
          <Description>Does things.</Description>
          <Website>https://example.com/mod</Website>
        </fomod>
        """;

    [Fact]
    public void TryParse_FillsIdentityMetadataFromInfoXml()
    {
        var parser = new FomodInstallerParser();
        var module = parser.TryParse(MinimalConfig, InfoXml);

        Assert.NotNull(module);
        Assert.Equal("Some Author", module!.Author);
        Assert.Equal("1.2.3", module.Version);
        Assert.Equal("Does things.", module.Description);
        Assert.Equal("https://example.com/mod", module.Website);
    }

    [Fact]
    public void TryParse_WithoutInfoXml_LeavesMetadataNull()
    {
        var parser = new FomodInstallerParser();
        var module = parser.TryParse(MinimalConfig);

        Assert.NotNull(module);
        Assert.Equal("Test Mod", module!.Name);
        Assert.Null(module.Author);
        Assert.Null(module.Version);
        Assert.Null(module.Description);
        Assert.Null(module.Website);
    }

    [Fact]
    public void TryParse_MalformedInfoXml_DoesNotThrowAndIgnoresMetadata()
    {
        var parser = new FomodInstallerParser();
        var module = parser.TryParse(MinimalConfig, "<fomod><Author>broken");

        // A malformed info.xml degrades to "no metadata"; the ModuleConfig still parses.
        Assert.NotNull(module);
        Assert.Equal("Test Mod", module!.Name);
        Assert.Null(module.Author);
    }

    [Fact]
    public void TryParse_InfoXmlWithoutFomodRoot_IsIgnored()
    {
        var parser = new FomodInstallerParser();
        var module = parser.TryParse(MinimalConfig, "<other><Author>Nope</Author></other>");

        Assert.NotNull(module);
        Assert.Null(module!.Author);
    }

    [Fact]
    public void TryParse_ParsesDefaultSelectedAttribute()
    {
        const string config = """
            <config>
              <moduleName>Defaults Mod</moduleName>
              <installSteps>
                <installStep name="Step">
                  <optionalFileGroups>
                    <group name="Texture set" type="SelectExactlyOne">
                      <plugins>
                        <plugin name="2K" defaultSelected="true">
                          <description>Default texture set.</description>
                          <files><folder source="Options/2K" destination="Textures"/></files>
                          <typeDescriptor><type name="Recommended"/></typeDescriptor>
                        </plugin>
                        <plugin name="4K">
                          <description>Upscaled texture set.</description>
                          <files><folder source="Options/4K" destination="Textures"/></files>
                          <typeDescriptor><type name="Optional"/></typeDescriptor>
                        </plugin>
                        <plugin name="HD" defaultSelected="false">
                          <description>Explicitly not the default.</description>
                          <files><folder source="Options/HD" destination="Textures"/></files>
                          <typeDescriptor><type name="Optional"/></typeDescriptor>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;

        var parser = new FomodInstallerParser();
        var module = parser.TryParse(config);

        Assert.NotNull(module);
        var group = module!.InstallSteps.Single().Groups.Single();
        Assert.True(group.Plugins.Single(plugin => plugin.Name == "2K").DefaultSelected);
        Assert.False(group.Plugins.Single(plugin => plugin.Name == "4K").DefaultSelected);
        Assert.False(group.Plugins.Single(plugin => plugin.Name == "HD").DefaultSelected);
    }

    [Fact]
    public void TryParse_ParsesPluginLevelVisibleDependency()
    {
        const string config = """
            <config>
              <moduleName>Visible Mod</moduleName>
              <installSteps>
                <installStep name="Step">
                  <optionalFileGroups>
                    <group name="Textures" type="SelectAny">
                      <plugins>
                        <plugin name="4K textures">
                          <visible>
                            <flagDependency flag="preset" value="4k"/>
                          </visible>
                        </plugin>
                        <plugin name="2K textures"/>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;

        var parser = new FomodInstallerParser();
        var module = parser.TryParse(config);

        Assert.NotNull(module);
        var plugins = module!.InstallSteps.Single().Groups.Single().Plugins;
        Assert.NotNull(plugins.Single(plugin => plugin.Name == "4K textures").VisibilityDependency);
        Assert.Null(plugins.Single(plugin => plugin.Name == "2K textures").VisibilityDependency);
    }

    [Fact]
    public void TryParse_ParsesAlwaysInstallAndInstallIfUsable()
    {
        const string config = """
            <config>
              <moduleName>Attrs Mod</moduleName>
              <installSteps>
                <installStep name="Step">
                  <optionalFileGroups>
                    <group name="Options" type="SelectAny">
                      <plugins>
                        <plugin name="Always">
                          <files>
                            <folder source="A" destination="A" alwaysInstall="true"/>
                            <folder source="B" destination="B" installIfUsable="true"/>
                            <folder source="C" destination="C"/>
                          </files>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;

        var module = new FomodInstallerParser().TryParse(config)!;
        var files = module.InstallSteps.Single().Groups.Single().Plugins.Single().Files;
        var always = files.Single(file => file.Source == "A");
        var ifUsable = files.Single(file => file.Source == "B");
        var plain = files.Single(file => file.Source == "C");

        Assert.True(always.AlwaysInstall);
        Assert.False(always.InstallIfUsable);
        Assert.True(ifUsable.InstallIfUsable);
        Assert.False(ifUsable.AlwaysInstall);
        Assert.False(plain.AlwaysInstall);
        Assert.False(plain.InstallIfUsable);
    }

    [Fact]
    public void TryParse_ParsesVersionAttributesOnGameDependency()
    {
        const string config = """
            <config>
              <moduleName>Version Mod</moduleName>
              <installSteps>
                <installStep name="Step">
                  <optionalFileGroups>
                    <group name="Options" type="SelectAny">
                      <plugins>
                        <plugin name="Gated">
                          <typeDescriptor>
                            <dependencyType>
                              <patterns>
                                <pattern>
                                  <dependencies>
                                    <gameDependency version="&lt;= 1.6.1130.0"/>
                                  </dependencies>
                                  <type name="NotUsable"/>
                                </pattern>
                              </patterns>
                            </dependencyType>
                          </typeDescriptor>
                        </plugin>
                      </plugins>
                    </group>
                  </optionalFileGroups>
                </installStep>
              </installSteps>
            </config>
            """;

        var module = new FomodInstallerParser().TryParse(config)!;
        var pattern = module.InstallSteps.Single().Groups.Single().Plugins.Single().DependencyPatterns.Single();
        var gameDep = Assert.IsType<FomodCompositeDependency>(pattern.Dependency);
        var leaf = Assert.IsType<FomodGameDependency>(gameDep.Children.Single());
        Assert.NotNull(leaf.VersionSpec);
        Assert.Equal(FomodVersionOperator.LessOrEqual, leaf.VersionSpec!.Operator);
        Assert.Equal("1.6.1130.0", leaf.VersionSpec.RawValue);
    }

    [Fact]
    public void TryParse_ParsesModuleImageAndTitleCosmeticAttributes()
    {
        const string config = """
            <config>
              <moduleName position="Right" colour="FF00FF">Cosmetic Mod</moduleName>
              <moduleImage path="fomod/img.png" showImage="false" height="96"/>
            </config>
            """;

        var module = new FomodInstallerParser().TryParse(config)!;
        Assert.Equal("fomod/img.png", module.ModuleImage);
        Assert.True(module.ModuleImageHidden);
        Assert.Equal(96, module.ModuleImageHeight);
        Assert.Equal("Right", module.ModuleTitlePosition);
        Assert.Equal(0xFF00FF, module.ModuleTitleColor);
    }
}
