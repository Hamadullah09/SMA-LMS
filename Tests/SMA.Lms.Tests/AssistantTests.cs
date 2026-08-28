using Library_Management_system.Application.Assistant;
using Library_Management_system.Application.Policies;
using Microsoft.Extensions.Options;
using Xunit;

namespace SMA.Lms.Tests;

/// <summary>
/// Assistant title extraction (specification sections 12, 13).
///
/// The assistant must never invent a book, so what it decides the student *asked for* has to be
/// predictable. These pin the phrasings a student actually uses.
/// </summary>
public class AssistantTitleExtractionTests
{
    [Theory]
    [InlineData("Where is Clean Code?", "Clean Code")]
    [InlineData("Where can I find Clean Code?", "Clean Code")]
    [InlineData("Do you have Database System Concepts?", "Database System Concepts")]
    [InlineData("Do you have a copy of Dune?", "Dune")]
    [InlineData("Is there a copy of Foundation?", "Foundation")]
    [InlineData("Can I borrow Neuromancer?", "Neuromancer")]
    [InlineData("I am looking for Calculus", "Calculus")]
    [InlineData("looking for Calculus", "Calculus")]
    [InlineData("Find me Pride and Prejudice", "Pride and Prejudice")]
    [InlineData("search for Dune", "Dune")]
    [InlineData("show me Dune", "Dune")]
    public void Common_phrasings_yield_the_title(string question, string expected)
    {
        Assert.Equal(expected, LibraryAssistant.ExtractTitle(question));
    }

    [Fact]
    public void Quoted_titles_win_over_lead_in_stripping()
    {
        // Without this, "Where is" inside a title would be mangled.
        Assert.Equal("Where the Crawdads Sing",
            LibraryAssistant.ExtractTitle("Do you have \"Where the Crawdads Sing\"?"));
    }

    [Fact]
    public void Curly_quotes_are_handled()
    {
        Assert.Equal("Dune", LibraryAssistant.ExtractTitle("Do you have “Dune”?"));
    }

    [Fact]
    public void Trailing_library_phrasing_is_stripped()
    {
        Assert.Equal("Dune", LibraryAssistant.ExtractTitle("Where is Dune in the library?"));
    }

    [Fact]
    public void The_word_book_is_not_treated_as_part_of_the_title()
    {
        Assert.Equal("Dune", LibraryAssistant.ExtractTitle("Where is the book Dune?"));
    }
}

/// <summary>
/// Intent routing. An account question misrouted to the catalogue produces the actively wrong
/// answer "I could not find that book" — which is how this was caught in live testing.
/// </summary>
public class AssistantIntentTests
{
    private static async Task<string> IntentOf(string question)
    {
        // No database is needed: an account question short-circuits before any query when the
        // caller is anonymous, which is exactly the routing decision under test.
        var assistant = new LibraryAssistant(null!, null!, Options.Create(new LibraryHoursOptions()));
        var answer = await assistant.AskAsync(question, studentId: null);
        return answer.Intent;
    }

    [Theory]
    [InlineData("What books do I have?")]      // plural — the bug this test was written for
    [InlineData("What book do I have?")]
    [InlineData("Do I owe any fines?")]
    [InlineData("Do I have any loans?")]
    [InlineData("What are my reservations?")]
    [InlineData("When do I have to return my books?")]
    [InlineData("Am I allowed to borrow more books?")]
    public async Task Account_questions_route_to_the_account_intent(string question)
    {
        Assert.Equal("account", await IntentOf(question));
    }

    [Fact]
    public async Task An_anonymous_account_question_asks_the_student_to_sign_in()
    {
        var assistant = new LibraryAssistant(null!, null!, Options.Create(new LibraryHoursOptions()));
        var answer = await assistant.AskAsync("What books do I have?", studentId: null);

        Assert.True(answer.RequiresSignIn);
        // It must not claim the library has no such book.
        Assert.DoesNotContain("could not find", answer.Text);
    }

    [Fact]
    public async Task A_question_too_short_to_act_on_is_not_guessed_at()
    {
        var assistant = new LibraryAssistant(null!, null!, Options.Create(new LibraryHoursOptions()));
        Assert.Equal("empty", (await assistant.AskAsync("a", null)).Intent);
    }
}
