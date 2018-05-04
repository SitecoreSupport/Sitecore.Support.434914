namespace Sitecore.Support.Workflows.Simple
{
  using Sitecore;
  using Sitecore.Data.Items;
  using Sitecore.Data.Templates;
  using Sitecore.Diagnostics;
  using Sitecore.Globalization;
  using Sitecore.Reflection;
  using Sitecore.Security.AccessControl;
  using Sitecore.StringExtensions;
  using Sitecore.Workflows;
  using Sitecore.Workflows.Simple;
  using System;
  using System.Collections;
  using System.Runtime.Serialization;

  public class Workflow : Sitecore.Workflows.Simple.Workflow
  {
    public Workflow(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }

    public Workflow(string workflowID, Sitecore.Workflows.Simple.WorkflowProvider owner) : base(workflowID, owner)
    {
    }

    public override WorkflowCommand[] GetCommands(string stateID, Item item)
    {
      Assert.ArgumentNotNullOrEmpty(stateID, "stateID");
      Item stateItem = this.GetStateItem(stateID);
      if (stateItem == null)
      {
        return new WorkflowCommand[0];
      }
      Item[] itemArray = stateItem.Children.ToArray();
      ArrayList list = new ArrayList();
      for (int i = 0; i < itemArray.Length; i++)
      {
        Item entity = itemArray[i];
        if (entity != null)
        {
          Template template = entity.Database.Engines.TemplateEngine.GetTemplate(entity.TemplateID);
          if (((template != null) && template.DescendsFromOrEquals(TemplateIDs.WorkflowCommand)) && AuthorizationManager.IsAllowed(entity, AccessRight.WorkflowCommandExecute, Context.User))
          {
            string displayName = entity.DisplayName;
            string icon = entity.Appearance.Icon;
            bool hasUI = false;
            //The fix: check whether supress comment option is selected
            bool suppressComment = entity["suppress comment"] == "1";
            if (!string.IsNullOrEmpty(entity[FieldIDs.AppearanceEvaluatorType]))
            {
              IWorkflowCommandAppearanceEvaluator evaluator = ReflectionUtil.CreateObject(entity[FieldIDs.AppearanceEvaluatorType]) as IWorkflowCommandAppearanceEvaluator;
              if (evaluator != null)
              {
                displayName = evaluator.GetCommandName(item, entity);
                icon = evaluator.GetCommandIcon(item, entity);
                hasUI = evaluator.HasUI(item, entity);
                suppressComment = evaluator.SuppressComments(item, entity);
              }
              else
              {
                Log.Error(Translate.Text("Type \"{0}\" not found.").FormatWith(new object[] { entity[FieldIDs.AppearanceEvaluatorType] }), this);
              }
            }
            list.Add(new WorkflowCommand(entity.ID.ToString(), displayName, icon, hasUI, suppressComment));
          }
        }
      }
      return (WorkflowCommand[])list.ToArray(typeof(WorkflowCommand));
    }
  }
}
