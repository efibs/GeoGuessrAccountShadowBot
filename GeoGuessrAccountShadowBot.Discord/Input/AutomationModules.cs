// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using GeoGuessrAccountShadowBot.Application.Automation;
using GeoGuessrAccountShadowBot.Discord.Output;
using GeoGuessrAccountShadowBot.Discord.Rendering;
using Microsoft.Extensions.Hosting;

namespace GeoGuessrAccountShadowBot.Discord.Input;

/// <summary>
/// The <c>/automation</c> commands. Authorization happens in the application layer, and every
/// answer is ephemeral, so rules and refusals stay between the bot and whoever asked.
/// </summary>
[Group("automation", "Rules that act as the GeoGuessr account by themselves")]
public sealed class AutomationModule(AutomationService automations, IHostApplicationLifetime lifetime)
    : InteractionModuleBase<SocketInteractionContext>
{
    /// <summary>The longest text a form field takes.</summary>
    private const int MaxRuleLength = 4000;

    private const string Placeholder = """
        {
          "Name": "welcome-new-friends",
          "On": "FriendRequestAccepted",
          "Do": [{ "SendDm": "Hi {{ player.nick }}, thanks!" }]
        }
        """;

    private CancellationToken Stopping => lifetime.ApplicationStopping;

    [SlashCommand("list", "List the automations")]
    public async Task ListAsync()
    {
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        await ReplyAsync(await automations.ListAsync(Context.User.Id, Stopping).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [SlashCommand("show", "Show an automation's rule and its recent runs")]
    public async Task ShowAsync([Autocomplete(typeof(RuleNameAutocomplete)), Summary("name", "The automation")] string name)
    {
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        await ReplyAsync(await automations.ShowAsync(Context.User.Id, name, Stopping).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [SlashCommand("create", "Create an automation from a JSON rule")]
    public Task CreateAsync() => RespondWithModalAsync(RuleForm(AutomationButtonIds.CreateForm, "New automation", null));

    [SlashCommand("edit", "Change an automation created from Discord")]
    public async Task EditAsync([Autocomplete(typeof(RuleNameAutocomplete)), Summary("name", "The automation")] string name)
    {
        var (json, refusal) = await automations.GetEditableAsync(Context.User.Id, name, Stopping).ConfigureAwait(false);
        if (refusal is not null)
        {
            await RespondAsync(AutomationRenderer.ToDiscord(refusal.Text), ephemeral: true, allowedMentions: MentionPolicy.Nobody()).ConfigureAwait(false);
            return;
        }

        if (json!.Length > MaxRuleLength)
        {
            await RespondAsync($"This rule has {json.Length} characters, more than a form holds. Recreate it in smaller rules chained with Raise.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        await RespondWithModalAsync(RuleForm(AutomationButtonIds.EditForm(name), $"Edit {name}", json)).ConfigureAwait(false);
    }

    [SlashCommand("enable", "Switch an automation on")]
    public Task EnableAsync([Autocomplete(typeof(RuleNameAutocomplete)), Summary("name", "The automation")] string name) =>
        RunAsync(() => automations.SetEnabledAsync(Context.User.Id, name, enabled: true, Stopping));

    [SlashCommand("disable", "Switch an automation off, and cancel its unfinished runs")]
    public Task DisableAsync([Autocomplete(typeof(RuleNameAutocomplete)), Summary("name", "The automation")] string name) =>
        RunAsync(() => automations.SetEnabledAsync(Context.User.Id, name, enabled: false, Stopping));

    [SlashCommand("delete", "Delete an automation created from Discord")]
    public Task DeleteAsync([Autocomplete(typeof(RuleNameAutocomplete)), Summary("name", "The automation")] string name) =>
        RunAsync(() => automations.DeleteAsync(Context.User.Id, name, Stopping));

    [SlashCommand("test", "Show what an automation would do, without doing anything")]
    public Task TestAsync(
        [Autocomplete(typeof(RuleNameAutocomplete)), Summary("name", "The automation")] string name,
        [Summary("player", "A GeoGuessr user id or profile link, to act as the event's player")] string? player = null,
        [Summary("text", "A message text, for rules that read event.text")] string? text = null) =>
        RunAsync(() => automations.TestAsync(Context.User.Id, name, player, text, Stopping));

    [SlashCommand("run", "Run an automation now. Its condition applies, its age and limits don't")]
    public Task RunNowAsync(
        [Autocomplete(typeof(RuleNameAutocomplete)), Summary("name", "The automation")] string name,
        [Summary("player", "A GeoGuessr user id or profile link, to act as the event's player")] string? player = null,
        [Summary("text", "A message text, for rules that read event.text")] string? text = null) =>
        RunAsync(() => automations.RunAsync(Context.User.Id, name, player, text, Stopping));

    [SlashCommand("runs", "Show recent automation runs")]
    public Task RunsAsync([Autocomplete(typeof(RuleNameAutocomplete)), Summary("name", "Only this automation")] string? name = null) =>
        RunAsync(() => automations.RunsAsync(Context.User.Id, name, Stopping));

    [SlashCommand("cancel", "Cancel an unfinished run")]
    public Task CancelAsync([Summary("run", "The run number, from /automation runs")] long run) =>
        RunAsync(() => automations.CancelAsync(Context.User.Id, run, Stopping));

    private async Task RunAsync(Func<Task<AutomationReply>> action)
    {
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        await ReplyAsync(await action().ConfigureAwait(false)).ConfigureAwait(false);
    }

    private Task ReplyAsync(AutomationReply reply) => AutomationReplies.FollowupAsync(Context.Interaction, reply);

    private static Modal RuleForm(string customId, string title, string? json) => new ModalBuilder()
        .WithTitle(title.Length > 45 ? title[..45] : title)
        .WithCustomId(customId)
        .AddTextInput("Rule (JSON)", AutomationButtonIds.RuleField, TextInputStyle.Paragraph, placeholder: json is null ? Placeholder : null, maxLength: MaxRuleLength, required: true, value: json)
        .Build();
}

/// <summary>Sends an automation answer, attaching it when it's too long for a message.</summary>
internal static class AutomationReplies
{
    private const int MessageLimit = 1900;

    public static async Task FollowupAsync(IDiscordInteraction interaction, AutomationReply reply)
    {
        var text = AutomationRenderer.ToDiscord(reply.Text);

        if (reply.Attachment is { } attachment)
        {
            using var file = new MemoryStream(Encoding.UTF8.GetBytes(attachment));
            await interaction.FollowupWithFileAsync(file, "rule.json", Clip(text), ephemeral: true, allowedMentions: MentionPolicy.Nobody()).ConfigureAwait(false);
            return;
        }

        if (text.Length <= MessageLimit)
        {
            await interaction.FollowupAsync(text, ephemeral: true, allowedMentions: MentionPolicy.Nobody()).ConfigureAwait(false);
            return;
        }

        using var full = new MemoryStream(Encoding.UTF8.GetBytes(text));
        await interaction.FollowupWithFileAsync(full, "reply.md", "The answer is long, so it's attached.", ephemeral: true, allowedMentions: MentionPolicy.Nobody()).ConfigureAwait(false);
    }

    private static string Clip(string text) => text.Length <= MessageLimit ? text : MessageChunker.Chunk(text, MessageLimit)[0];
}

/// <summary>
/// The forms and buttons of automations. They live outside the command group, so their ids aren't
/// prefixed with the group name.
/// </summary>
public sealed class AutomationInteractionsModule(AutomationService automations, IHostApplicationLifetime lifetime)
    : InteractionModuleBase<SocketInteractionContext>
{
    private CancellationToken Stopping => lifetime.ApplicationStopping;

    // Discord.Net only accepts a modal handler whose last parameter is the modal.
    [ModalInteraction(AutomationButtonIds.CreateForm)]
    public Task CreateAsync(AutomationRuleForm form) => SaveAsync(null, form);

    [ModalInteraction(AutomationButtonIds.EditFormPrefix + "*")]
    public Task EditAsync(string name, AutomationRuleForm form) => SaveAsync(name, form);

    [ComponentInteraction("au:ok:*")]
    public Task ApproveAsync(long runId) => DecideAsync(runId, approve: true);

    [ComponentInteraction("au:no:*")]
    public Task RejectAsync(long runId) => DecideAsync(runId, approve: false);

    private async Task SaveAsync(string? editing, AutomationRuleForm form)
    {
        await DeferAsync(ephemeral: true).ConfigureAwait(false);

        var json = form.Rule;

        var reply = string.IsNullOrWhiteSpace(json)
            ? new AutomationReply(false, "The form was empty.")
            : await automations.SaveAsync(Context.User.Id, json, editing, Stopping).ConfigureAwait(false);

        await AutomationReplies.FollowupAsync(Context.Interaction, reply).ConfigureAwait(false);
    }

    private async Task DecideAsync(long runId, bool approve)
    {
        await DeferAsync(ephemeral: true).ConfigureAwait(false);
        var reply = await automations.DecideAsync(Context.User.Id, runId, approve, Stopping).ConfigureAwait(false);
        await AutomationReplies.FollowupAsync(Context.Interaction, reply).ConfigureAwait(false);
    }
}

/// <summary>
/// The rule form, as Discord.Net reads it back. The form that's shown is built by
/// <see cref="AutomationModule"/>, with the rule filled in when editing.
/// </summary>
public sealed class AutomationRuleForm : IModal
{
    public string Title => "Automation";

    [ModalTextInput(AutomationButtonIds.RuleField, TextInputStyle.Paragraph)]
    public string? Rule { get; set; }
}

/// <summary>Suggests rule names, to people who may see the rules.</summary>
public sealed class RuleNameAutocomplete(AutomationService automations) : AutocompleteHandler
{
    private const int MaxSuggestions = 25;

    public override Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        var typed = autocompleteInteraction.Data.Current.Value?.ToString() ?? "";
        var suggestions = automations.RuleNames
            .Where(name => name.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Take(MaxSuggestions)
            .Select(name => new AutocompleteResult(name, name));

        return Task.FromResult(AutocompletionResult.FromSuccess(suggestions));
    }
}
