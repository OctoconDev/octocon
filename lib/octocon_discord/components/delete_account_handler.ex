defmodule OctoconDiscord.Components.DeleteAccountHandler do
  @moduledoc false
  use GenServer

  alias Nostrum.Struct.Component.{
    ActionRow,
    Button
  }

  alias Nostrum.Api

  alias Octocon.Accounts
  alias OctoconDiscord.Utils

  @table :delete_account

  def start_link([]) do
    GenServer.start_link(__MODULE__, [], name: __MODULE__)
  end

  def handle_init(system_identity) do
    user = Accounts.get_user!(system_identity)

    uid = :erlang.unique_integer([:positive])

    data = %{
      system_id: user.id,
      uid: uid,
      confirmations_left: 3
    }

    :ets.insert(@table, {uid, data})

    Process.send_after(__MODULE__, {:drop, uid}, :timer.minutes(5))

    Utils.send_dm(user, generate_response(data))
  end

  defp generate_response(%{
         system_id: _system_id,
         uid: uid,
         confirmations_left: confirmations_left
       })
       when confirmations_left > 0 do
    [
      embeds: [
        %Nostrum.Struct.Embed{
          title: "Delete account",
          color: Utils.hex_to_int("#FF0000"),
          description:
            "**WARNING**: This command will **permanently** delete your system, as well as all associated data and settings.\n\nTo confirm, click the button below a total of **3** times."
        }
      ],
      components: [
        ActionRow.action_row([
          Button.interaction_button(
            "Confirm (#{3 - confirmations_left}/3)",
            "delete-account|confirm|#{uid}",
            style: 4,
            emoji: %Nostrum.Struct.Emoji{name: "⚠️"}
          )
        ])
      ]
    ]
  end

  defp generate_response(%{
         system_id: system_id,
         uid: _uid,
         confirmations_left: _confirmations_left
       }) do
    Accounts.delete_user({:system, system_id})

    [
      embeds: [
        %Nostrum.Struct.Embed{
          title: ":wave: Success!",
          color: Utils.hex_to_int("#00FF00"),
          description:
            "**Your system has been deleted**. We hope you enjoyed using Octocon!\n\nIf you'd like to create a new account, you can run the `/register` command.\n\nIf you have any feedback, questions, or concerns, feel free to join our community server: https://octocon.app/discord"
        }
      ],
      components: []
    ]
  end

  def handle_interaction("confirm", uid, interaction) do
    old_data =
      :ets.lookup(@table, uid)
      |> hd()
      |> elem(1)

    data = %{
      old_data
      | confirmations_left: old_data.confirmations_left - 1
    }

    :ets.insert(@table, {uid, data})

    Api.create_interaction_response(interaction, %{
      type: 7,
      data: generate_response(data) |> Enum.into(%{})
    })
  end

  def drop(uid) do
    :ets.delete(@table, uid)
  end

  def init([]) do
    :ets.new(@table, [
      :set,
      :named_table,
      :public,
      read_concurrency: true,
      write_concurrency: :auto,
      decentralized_counters: true
    ])

    {:ok, %{}}
  end

  def handle_info({:drop, uid}, state) do
    :ets.delete(@table, uid)
    {:noreply, state}
  end
end
