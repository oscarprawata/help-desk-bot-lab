﻿namespace Exercise8.Dialogs
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using AdaptiveCards;
    using Autofac;
    using Exercise8.Util;
    using Microsoft.Bot.Builder.ConnectorEx;
    using Microsoft.Bot.Builder.Dialogs;
    using Microsoft.Bot.Builder.Luis;
    using Microsoft.Bot.Builder.Luis.Models;
    using Microsoft.Bot.Connector;
    using Exercise8.Services;

    [LuisModel("38ffac05-8cc5-493f-b4f6-dda46be5554c", "d2cb269172684db6bebc43b695a82d1c")]
    [Serializable]
    public class RootDialog : LuisDialog<object>
    {
        private string category;
        private string severity;
        private string description;

        [LuisIntent("")]
        [LuisIntent("None")]
        public async Task None(IDialogContext context, LuisResult result)
        {
            await context.PostAsync($"I'm sorry, I did not understand '{result.Query}'.\nType 'help' to know more about me :)");
            context.Done<object>(null);
        }

        [LuisIntent("Help")]
        public async Task Help(IDialogContext context, LuisResult result)
        {
            await context.PostAsync("I'm the help desk bot and I can help you create a ticket or explore the knowledge base.\n" +
                        "You can tell me things like _I need to reset my password_ or _explore hardware articles_.");
            context.Done<object>(null);
        }

        [LuisIntent("HandOffToHuman")]
        public async Task HandOff(IDialogContext context, LuisResult result)
        {
            var conversationReference = context.Activity.ToConversationReference();
            var provider = Conversation.Container.Resolve<HandOff.Provider>();

            if (provider.QueueMe(conversationReference))
            {
                var waitingPeople = provider.Pending() > 1 ? $", there are { provider.Pending() - 1 }" : string.Empty;

                await context.PostAsync($"Connecting you to the next available human agent...please wait{waitingPeople}.");
            }
            
            context.Done<object>(null);
        }

        [LuisIntent("SubmitTicket")]
        public async Task SubmitTicket(IDialogContext context, IAwaitable<IMessageActivity> activityWaiter, LuisResult result)
        {
            var activity = await activityWaiter;
            EntityRecommendation categoryEntityRecommendation, severityEntityRecommendation;

            result.TryFindEntity("category", out categoryEntityRecommendation);
            result.TryFindEntity("severity", out severityEntityRecommendation);

            this.category = ((Newtonsoft.Json.Linq.JArray)categoryEntityRecommendation?.Resolution["values"])?[0]?.ToString();
            this.severity = ((Newtonsoft.Json.Linq.JArray)severityEntityRecommendation?.Resolution["values"])?[0]?.ToString();
            this.description = result.Query;

            await this.EnsureTicket(context);

            await this.SendSearchToBackchannel(context, activity, result.Query);
        }

        [LuisIntent("ExploreKnowledgeBase")]
        public async Task ExploreCategory(IDialogContext context, LuisResult result)
        {
            EntityRecommendation categoryEntityRecommendation;
            result.TryFindEntity("category", out categoryEntityRecommendation);
            var category = ((Newtonsoft.Json.Linq.JArray)categoryEntityRecommendation?.Resolution["values"])?[0]?.ToString();

            context.Call(new CategoryExplorerDialog(category, result.Query), this.ResumeAndEndDialogAsync);
        }

        private async Task ResumeAndEndDialogAsync(IDialogContext context, IAwaitable<object> argument)
        {
            context.Done<object>(null);
        }

        private async Task EnsureTicket(IDialogContext context)
        {
            if (this.severity == null)
            {
                var severities = new string[] { "high", "normal", "low" };
                PromptDialog.Choice(context, this.SeverityMessageReceivedAsync, severities, "Which is the severity of this problem?", null, 3, PromptStyle.AutoText);
            }
            else if (this.category == null)
            {
                PromptDialog.Text(context, this.CategoryMessageReceivedAsync, "Which would be the category for this ticket(software, hardware, network, and so on) ?");
            }
            else
            {
                var text = $"Great!I'm going to create a **{this.severity}** severity ticket in the **{this.category}** category. " +
                       $"The description I will use is _\"{this.description}\"_.Can you please confirm that this information is correct?";

                PromptDialog.Confirm(context, this.IssueConfirmedMessageReceivedAsync, text, null, 3, PromptStyle.AutoText);
            }
        }

        private async Task SeverityMessageReceivedAsync(IDialogContext context, IAwaitable<string> argument)
        {
            this.severity = await argument;
            await this.EnsureTicket(context);
        }

        private async Task CategoryMessageReceivedAsync(IDialogContext context, IAwaitable<string> argument)
        {
            this.category = await argument;
            await this.EnsureTicket(context);
        }

        private async Task IssueConfirmedMessageReceivedAsync(IDialogContext context, IAwaitable<bool> argument)
        {
            var confirmed = await argument;

            if (confirmed)
            {
                var api = new TicketAPIClient();
                var ticketId = await api.PostTicketAsync(this.category, this.severity, this.description);

                if (ticketId != -1)
                {
                    var message = context.MakeMessage();
                    message.Attachments = new List<Attachment>
                    {
                        new Attachment
                        {
                            ContentType = "application/vnd.microsoft.card.adaptive",
                            Content = this.CreateCard(ticketId, this.category, this.severity, this.description)
                        }
                    };
                    await context.PostAsync(message);
                }
                else
                {
                    await context.PostAsync("Ooops! Something went wrong while I was saving your ticket. Please try again later.");
                }

                context.Call(new UserFeedbackRequestDialog(), this.ResumeAndEndDialogAsync);
            }
            else
            {
                await context.PostAsync("Ok. The ticket was not created. You can start again if you want.");
                context.Done<object>(null);
            }
        }

        private async Task SendSearchToBackchannel(IDialogContext context, IMessageActivity activity, string textSearch)
        {
            var searchService = new AzureSearchService();
            var searchResult = await searchService.Search(textSearch);
            if (searchResult != null && searchResult.Value.Length != 0)
            {
                var reply = ((Activity)activity).CreateReply();

                reply.Type = ActivityTypes.Event;
                reply.Name = "searchResults";
                reply.Value = searchResult.Value;
                await context.PostAsync(reply);
            }
        }

        private AdaptiveCard CreateCard(int ticketId, string category, string severity, string description)
        {
            AdaptiveCard card = new AdaptiveCard();

            var headerBlock = new TextBlock()
            {
                Text = $"Issue #{ticketId}",
                Weight = TextWeight.Bolder,
                Size = TextSize.Large,
                Speak = $"<s>You've created a new issue #{ticketId}</s><s>We will contact you soon.</s>"
            };

            var columnsBlock = new ColumnSet()
            {
                Separation = SeparationStyle.Strong,
                Columns = new List<Column>
                {
                    new Column
                    {
                        Size = "1",
                        Items = new List<CardElement>
                        {
                            new FactSet
                            {
                                Facts = new List<AdaptiveCards.Fact>
                                {
                                    new AdaptiveCards.Fact("Severity:", severity),
                                    new AdaptiveCards.Fact("Category:", category),
                                }
                            }
                        }
                    },
                    new Column
                    {
                        Size = "auto",
                        Items = new List<CardElement>
                        {
                            new Image
                            {
                                Url = "http://i.imgur.com/WPdnJg8.png",
                                Size = ImageSize.Small,
                                HorizontalAlignment = HorizontalAlignment.Right
                            }
                        }
                    }
                }
            };

            var descriptionBlock = new TextBlock
            {
                Text = description,
                Wrap = true
            };

            card.Body.Add(headerBlock);
            card.Body.Add(columnsBlock);
            card.Body.Add(descriptionBlock);

            return card;
        }
    }
}