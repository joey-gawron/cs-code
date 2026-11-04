using CsCode.SpellCheck.Engine;

namespace CsCode.SpellCheck.Tests;

public class DictionaryTests
{
    [Theory]
    [InlineData("receive", true)]
    [InlineData("Receive", true)]
    [InlineData("RECEIVE", true)]
    [InlineData("recieve", false)]
    [InlineData("doesn't", true)]
    [InlineData("doesn’t", true)]
    [InlineData("parser's", true)]
    [InlineData("APIs", true)]
    [InlineData("URLs", true)]
    [InlineData("colour", false)] // en_US: British spellings need an en_GB dictionary
    public void The_bundled_en_US_dictionary_knows_english_words(string word, bool known) =>
        Assert.Equal(known, Dictionaries.EnUs.IsKnown(word));

    [Theory]
    [InlineData("async")]
    [InlineData("config")]
    [InlineData("JSON")]
    [InlineData("Nullable")]
    [InlineData("enum")]
    [InlineData("stdout")]
    [InlineData("regex")]
    [InlineData("Http")]
    [InlineData("tokenizer")]
    [InlineData("constexpr")]
    [InlineData("kwargs")]
    public void The_software_terms_cover_common_programming_words_and_keywords(string word) =>
        Assert.True(Dictionaries.EnUs.IsKnown(word), word);

    /// <summary>Common misspellings, many of them of programming words: neither the dictionary nor the software terms may accept one.</summary>
    [Fact]
    public void Common_misspellings_are_not_hidden_by_the_software_terms()
    {
        string[] typos =
        [
            "recieve", "seperate", "occured", "definately", "wierd", "untill", "accross", "adress", "begining", "beleive", "calender",
            "comming", "commited", "enviroment", "existance", "goverment", "independant", "maintainance", "neccessary", "occurence",
            "paramter", "persistant", "priviledge", "recomend", "refered", "succesful", "threshhold", "tommorow", "truely", "writting",
            "lenght", "retreive", "intialize", "asyncronous", "syncronous", "seperator", "delimeter", "dependancy", "dependancies",
            "compatability", "accomodate", "aquire", "arguement", "assigment", "availible", "catagory", "dissapear", "implmentation",
            "inital", "instanciate", "interupt", "lisence", "mispell", "parrallel", "preceeding", "prefered", "proccess", "reponse",
            "resouce", "responce", "sucess", "supress", "tranform", "transfered", "unecessary", "usefull", "varaible", "verison",
            "funtion", "fucntion", "retrun", "pubilc", "pirvate", "stirng", "lenth", "widht", "heigth", "conifg", "cofnig", "tokne",
            "reigster", "regsiter", "udpate", "upadte", "veiw", "verticle", "wether", "wich", "teh",
        ];
        var set = Dictionaries.EnUs;
        var accepted = typos.Where(t => set.IsKnown(t)).ToArray();
        Assert.True(accepted.Length == 0, "Accepted misspellings: " + string.Join(", ", accepted));
    }

    [Fact]
    public void Suggestions_come_best_first_in_the_words_case()
    {
        var set = Dictionaries.EnUs;
        Assert.Equal("receive", set.Suggest("recieve", 5)[0]);
        Assert.Equal("Receive", set.Suggest("Recieve", 5)[0]);
        Assert.Equal("RECEIVE", set.Suggest("RECIEVE", 5)[0]);
        Assert.True(set.Suggest("teh", 5).Count <= 5);
    }

    [Fact]
    public void Language_entries_choose_dictionaries_by_name()
    {
        var available = new[]
        {
            new DictionarySource("en_US", "en_us", null, null, 1, DateTime.MinValue),
            new DictionarySource("en_GB", "en_gb", "/d/en_GB.dic", "/d/en_GB.aff", 1, DateTime.MinValue),
            new DictionarySource("de_AT", "de_at", "/d/de_AT.dic", "/d/de_AT.aff", 1, DateTime.MinValue),
            new DictionarySource("de_DE", "de_de", "/d/de_DE.dic", "/d/de_DE.aff", 1, DateTime.MinValue),
        };
        Assert.Equal(["en_US"], DictionaryCatalog.Choose(available, ["en"]).Chosen.Select(c => c.Name));
        Assert.Equal(["en_US", "en_GB"], DictionaryCatalog.Choose(available, ["en", "en-GB"]).Chosen.Select(c => c.Name));
        Assert.Equal(["de_DE"], DictionaryCatalog.Choose(available, ["de"]).Chosen.Select(c => c.Name));
        var (chosen, missing) = DictionaryCatalog.Choose(available, ["fr", "en"]);
        Assert.Equal(["en_US"], chosen.Select(c => c.Name));
        Assert.Equal(["fr"], missing);
    }

    [Fact]
    public void User_dictionaries_are_dic_and_aff_pairs_in_the_folder()
    {
        using var dir = new CsCode.TestDoubles.TempDirectory("cscode-spell-dict-");
        dir.WriteFile("xx_YY.dic", "2\nflurble\nzorp\n");
        dir.WriteFile("xx_YY.aff", "SET UTF-8\n");
        dir.WriteFile("lonely.dic", "1\nnothing\n"); // no .aff: not offered
        var available = DictionaryCatalog.Discover(dir.Path);
        Assert.Equal(["en_US", "xx_YY"], available.Select(a => a.Name));
        var (chosen, _) = DictionaryCatalog.Choose(available, ["xx"]);
        var list = DictionaryCatalog.Load(chosen.Single());
        Assert.True(list.Check("flurble"));
        Assert.False(list.Check("receive"));
    }
}
