defmodule OctoconDiscord.Components do
  @moduledoc false

  alias __MODULE__.{
    AlterPaginator,
    DeleteAccountHandler,
    WipeAltersHandler,
    HelpHandler
  }

  @dispatchers %{
    "alter" => &AlterPaginator.handle_interaction/3,
    "wipe-alters" => &WipeAltersHandler.handle_interaction/3,
    "delete-account" => &DeleteAccountHandler.handle_interaction/3,
    "help" => &HelpHandler.handle_interaction/3
  }

  def dispatch(interaction) do
    [type, action, uid] = String.split(interaction.data.custom_id, "|")

    Map.get(@dispatchers, type).(action, String.to_integer(uid), interaction)
  end
end
