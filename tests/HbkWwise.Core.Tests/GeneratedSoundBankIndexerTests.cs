using HbkWwise.Core;

namespace HbkWwise.Core.Tests;

public sealed class GeneratedSoundBankIndexerTests
{
    [Fact]
    public void Build_MapsStreamedPrefetchAndStatePath()
    {
        using var fixture = XmlFixture.Create();
        var index = new GeneratedSoundBankIndexer().Build(fixture.Path);

        var media = Assert.Single(index.FindMedia(6639526));

        Assert.Equal("Music_ST01", media.Bank);
        Assert.Equal("Music\\HBK_ST01_INTRO_1.wav", media.SourceName);
        Assert.True(media.IsStreamed);
        Assert.Equal(26732, media.PrefetchSize);

        var usage = Assert.Single(media.Uses);

        Assert.Contains("aRepetition=n01 / bComposition=_010_Intro", usage.StatePaths);
    }

    [Fact]
    public void Search_MatchesAllSourceNameTerms()
    {
        using var fixture = XmlFixture.Create();
        var index = new GeneratedSoundBankIndexer().Build(fixture.Path);

        var results = index.Search("st01 intro", new SearchOptions(MusicOnly: true));

        Assert.Equal(6639526u, Assert.Single(results).Id);
        Assert.Single(index.Search("6639526", new SearchOptions(Language: "SFX")));
        Assert.Empty(index.Search("6639526", new SearchOptions(Language: "English")));
    }

    [Fact]
    public void Build_ParsesEveryBankInAggregateXml()
    {
        using var fixture = XmlFixture.Create(AggregateXml);

        var index = new GeneratedSoundBankIndexer().Build(fixture.Path);

        Assert.Equal(2, index.Banks.Length);
        Assert.Contains(index.Banks, bank => bank.Name == "First");
        Assert.Contains(index.Banks, bank => bank.Name == "Second");
    }

    [Fact]
    public void BuildOverlay_LaterDirectoryReplacesMatchingXmlAndKeepsAdditions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hbkwwise-overlay-{Guid.NewGuid():N}");
        var baseDirectory = Path.Combine(root, "base");
        var updateDirectory = Path.Combine(root, "update");

        Directory.CreateDirectory(baseDirectory);
        Directory.CreateDirectory(updateDirectory);
        File.WriteAllText(Path.Combine(baseDirectory, "Shared.xml"), BankXml(1, "Old"));
        File.WriteAllText(Path.Combine(baseDirectory, "BaseOnly.xml"), BankXml(2, "BaseOnly"));
        File.WriteAllText(Path.Combine(updateDirectory, "Shared.xml"), BankXml(3, "Updated"));
        File.WriteAllText(Path.Combine(updateDirectory, "UpdateOnly.xml"), BankXml(4, "UpdateOnly"));

        try
        {
            var index = new GeneratedSoundBankIndexer().BuildOverlay([baseDirectory, updateDirectory]);

            Assert.Equal(["BaseOnly", "Updated", "UpdateOnly"], index.Banks.Select(bank => bank.Name));
            Assert.DoesNotContain(index.Banks, bank => bank.Name == "Old");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class XmlFixture : IDisposable
    {
        private XmlFixture(string path) => Path = path;

        public string Path { get; }

        public static XmlFixture Create(string xml = Xml)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hbkwwise-{Guid.NewGuid():N}.xml");
            File.WriteAllText(path, xml);

            return new XmlFixture(path);
        }

        public void Dispose() => File.Delete(Path);

        private const string Xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <SoundBanksInfo>
              <SoundBanks>
                <SoundBank Id="1901749289" GUID="{BANK}" Language="SFX">
                  <ShortName>Music_ST01</ShortName>
                  <Path>Music_ST01.bnk</Path>
                  <IncludedEvents>
                    <Event Id="1297021543" Name="Mu_Session_ST01A_Play" ObjectPath="\Events\Mu_Session_ST01A_Play" GUID="{EVENT}" DurationType="Infinite">
                      <ReferencedStreamedFiles>
                        <File Id="6639526" Language="SFX">
                          <ShortName>Music\HBK_ST01_INTRO_1.wav</ShortName>
                          <Path>SFX\Music\HBK_ST01_INTRO_1_HASH.wem</Path>
                        </File>
                      </ReferencedStreamedFiles>
                      <IncludedMemoryFiles>
                        <File Id="6639526" Language="SFX">
                          <ShortName>Music\HBK_ST01_INTRO_1.wav</ShortName>
                          <Path>SFX\Music\HBK_ST01_INTRO_1_HASH.wem</Path>
                          <PrefetchSize>26732</PrefetchSize>
                        </File>
                      </IncludedMemoryFiles>
                      <ActionSetState>
                        <ActionSetStateEntry Id="1736184856" Name="n01" ObjectPath="\States\Music\aRepetition\n01" GUID="{REP}" />
                        <ActionSetStateEntry Id="1" Name="_010_Intro" ObjectPath="\States\Music\bComposition\_010_Intro" GUID="{COMP}" />
                      </ActionSetState>
                      <SwitchContainers>
                        <SwitchContainer SwitchValue="{REP}">
                          <Children>
                            <SwitchContainer SwitchValue="{COMP}">
                              <Media><File Id="6639526" /></Media>
                            </SwitchContainer>
                          </Children>
                        </SwitchContainer>
                      </SwitchContainers>
                    </Event>
                  </IncludedEvents>
                </SoundBank>
              </SoundBanks>
            </SoundBanksInfo>
            """;
    }

    private const string AggregateXml = """
        <SoundBanksInfo>
          <SoundBanks>
            <SoundBank Id="1" Language="SFX"><ShortName>First</ShortName><Path>First.bnk</Path></SoundBank>
            <SoundBank Id="2" Language="SFX"><ShortName>Second</ShortName><Path>Second.bnk</Path></SoundBank>
          </SoundBanks>
        </SoundBanksInfo>
        """;

    private static string BankXml(uint id, string name) => $"""
        <SoundBanksInfo>
          <SoundBanks>
            <SoundBank Id="{id}" Language="SFX"><ShortName>{name}</ShortName><Path>{name}.bnk</Path></SoundBank>
          </SoundBanks>
        </SoundBanksInfo>
        """;
}
