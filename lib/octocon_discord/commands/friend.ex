defmodule OctoconDiscord.Commands.Friend do
  @moduledoc false

  @behaviour Nosedrum.ApplicationCommand

  require Logger

  alias Octocon.Friendships

  alias OctoconDiscord.Utils

  @subcommands %{
    "add" => &__MODULE__.add/2,
    "accept" => &__MODULE__.accept/2,
    "reject" => &__MODULE__.reject/2,
    "remove" => &__MODULE__.remove/2,
    "cancel" => &__MODULE__.cancel/2,
    "list" => &__MODULE__.list/2,
    "list-requests" => &__MODULE__.list_requests/2,
    "trust" => &__MODULE__.trust/2,
    "untrust" => &__MODULE__.untrust/2
  }

  @impl true
  def description, do: "Manages your friends."

  @impl true
  def command(interaction) do
    %{data: %{resolved: resolved}, user: %{id: discord_id}} = interaction
    discord_id = to_string(discord_id)

    Utils.ensure_registered(discord_id, fn ->
      %{data: %{options: [%{name: name, options: options}]}} = interaction

      @subcommands[name].(
        %{resolved: resolved, system_identity: {:discord, discord_id}},
        options
      )
    end)
  end

  def add(context, options) do
    opts = %{
      system_id: Utils.get_command_option(options, "system-id"),
      discord_id: Utils.get_command_option(options, "discord"),
      username: Utils.get_command_option(options, "username")
    }

    Utils.system_id_from_opts(opts, fn identity, decorator ->
      send_friend_request(context, identity, decorator)
    end)
  end

  def accept(context, options) do
    opts = %{
      system_id: Utils.get_command_option(options, "system-id"),
      discord_id: Utils.get_command_option(options, "discord"),
      username: Utils.get_command_option(options, "username")
    }

    Utils.system_id_from_opts(opts, fn identity, decorator ->
      accept_friend_request(context, identity, decorator)
    end)
  end

  def reject(context, options) do
    opts = %{
      system_id: Utils.get_command_option(options, "system-id"),
      discord_id: Utils.get_command_option(options, "discord"),
      username: Utils.get_command_option(options, "username")
    }

    Utils.system_id_from_opts(opts, fn identity, decorator ->
      reject_friend_request(context, identity, decorator)
    end)
  end

  def cancel(context, options) do
    opts = %{
      system_id: Utils.get_command_option(options, "system-id"),
      discord_id: Utils.get_command_option(options, "discord"),
      username: Utils.get_command_option(options, "username")
    }

    Utils.system_id_from_opts(opts, fn identity, decorator ->
      cancel_friend_request(context, identity, decorator)
    end)
  end

  def remove(context, options) do
    opts = %{
      system_id: Utils.get_command_option(options, "system-id"),
      discord_id: Utils.get_command_option(options, "discord"),
      username: Utils.get_command_option(options, "username")
    }

    Utils.system_id_from_opts(opts, fn identity, decorator ->
      remove_friend(context, identity, decorator)
    end)
  end

  def list(%{system_identity: system_identity}, _options) do
    case Friendships.list_friendships(system_identity) do
      [] ->
        Utils.error_embed("You have no friends (yet!). Add some with `/friend add`!")

      friendships ->
        [
          embeds: [
            %Nostrum.Struct.Embed{
              title: "Your friends (#{length(friendships)})",
              description:
                Enum.map_join(friendships, "\n", fn %{friend: friend, friendship: %{level: level}} ->
                  "- **#{friend.username || friend.id}** (#{case friend.discord_id do
                    nil -> ""
                    id -> "<@#{id}>"
                  end}#{case level do
                    :trusted_friend -> "; :star:"
                    :friend -> ""
                  end})"
                end),
              footer: %Nostrum.Struct.Embed.Footer{
                text: "⭐ = Trusted friend"
              }
            }
          ],
          ephemeral?: true
        ]
    end
  end

  def list_requests(%{system_identity: system_identity}, _options) do
    incoming_requests = Friendships.incoming_friend_requests(system_identity)
    outgoing_requests = Friendships.outgoing_friend_requests(system_identity)

    if incoming_requests == [] and outgoing_requests == [] do
      Utils.error_embed(
        "You don't have any incoming or outgoing friend requests. Add a friend with `/friend add`!"
      )
    else
      incoming_embed =
        if incoming_requests != [] do
          [
            %{
              title: "Incoming friend requests (#{length(incoming_requests)})",
              description:
                Enum.map_join(incoming_requests, "\n", fn %{
                                                            request: %{from_id: from_id},
                                                            from: %{
                                                              username: username,
                                                              discord_id: discord_id
                                                            }
                                                          } ->
                  "- **#{username || from_id}**#{case discord_id do
                    nil -> ""
                    _ -> " (<@#{discord_id}>)"
                  end}"
                end)
            }
          ]
        else
          []
        end

      outgoing_embed =
        if outgoing_requests != [] do
          [
            %{
              title: "Outgoing friend requests (#{length(outgoing_requests)})",
              description:
                Enum.map_join(outgoing_requests, "\n", fn %{
                                                            request: %{to_id: to_id},
                                                            to: %{
                                                              username: username,
                                                              discord_id: discord_id
                                                            }
                                                          } ->
                  "- **#{username || to_id}**#{case discord_id do
                    nil -> ""
                    _ -> " (<@#{discord_id}>)"
                  end}"
                end)
            }
          ]
        else
          []
        end

      [
        embeds: incoming_embed ++ outgoing_embed,
        ephemeral?: true
      ]
    end
  end

  def trust(context, options) do
    opts = %{
      system_id: Utils.get_command_option(options, "system-id"),
      discord_id: Utils.get_command_option(options, "discord"),
      username: Utils.get_command_option(options, "username")
    }

    Utils.system_id_from_opts(opts, fn identity, decorator ->
      trust_friend(context, identity, decorator)
    end)
  end

  def untrust(context, options) do
    opts = %{
      system_id: Utils.get_command_option(options, "system-id"),
      discord_id: Utils.get_command_option(options, "discord"),
      username: Utils.get_command_option(options, "username")
    }

    Utils.system_id_from_opts(opts, fn identity, decorator ->
      untrust_friend(context, identity, decorator)
    end)
  end

  defp trust_friend(%{system_identity: system_identity}, target_identity, decorator) do
    case Friendships.trust_friend(system_identity, target_identity) do
      :ok ->
        Utils.success_embed("#{decorator} is now a trusted friend!")

      {:error, :not_friends} ->
        Utils.error_embed("You are not friends with #{decorator}.")

      {:error, _} ->
        Utils.error_embed(
          "An unknown error occurred while trusting the friend. Please try again."
        )
    end
  end

  defp untrust_friend(%{system_identity: system_identity}, target_identity, decorator) do
    case Friendships.untrust_friend(system_identity, target_identity) do
      :ok ->
        Utils.success_embed("#{decorator} is no longer a trusted friend!")

      {:error, :not_friends} ->
        Utils.error_embed("You are not friends with #{decorator}.")

      {:error, _} ->
        Utils.error_embed(
          "An unknown error occurred while untrusting the friend. Please try again."
        )
    end
  end

  defp remove_friend(%{system_identity: system_identity}, target_identity, decorator) do
    case Friendships.remove_friendship(system_identity, target_identity) do
      :ok ->
        Utils.success_embed("You are no longer friends with #{decorator}!")

      {:error, :not_friends} ->
        Utils.error_embed("You are not friends with #{decorator}.")

      {:error, _} ->
        Utils.error_embed(
          "An unknown error occurred while removing the friendship. Please try again."
        )
    end
  end

  defp send_friend_request(%{system_identity: system_identity}, to_identity, decorator) do
    case Friendships.send_request(system_identity, to_identity) do
      {:ok, :sent} ->
        Utils.success_embed("Sent a friend request to #{decorator}.")

      {:ok, :accepted} ->
        Utils.success_embed(
          "You are now friends with #{decorator}!\n\nIf you'd like to add them as a trusted friend, use `/friend trust`."
        )

      {:error, :already_friends} ->
        Utils.error_embed("You are already friends with #{decorator}.")

      {:error, :already_sent_request} ->
        Utils.error_embed("You have already sent a friend request to #{decorator}.")

      {:error, %{errors: [to_id: {"does not exist", _}]}} ->
        Utils.error_embed("That system #{decorator} does not exist.")

      {:error, _} ->
        Utils.error_embed(
          "An unknown error occurred while sending the friend request. Please try again."
        )
    end
  end

  defp accept_friend_request(%{system_identity: system_identity}, from_identity, decorator) do
    case Friendships.accept_request(from_identity, system_identity) do
      :ok ->
        Utils.success_embed(
          "You are now friends with #{decorator}!\n\nIf you'd like to add them as a trusted friend, use `/friend trust`."
        )

      {:error, :not_requested} ->
        Utils.error_embed("You do not have an incoming friend request from #{decorator}.")

      {:error, %{errors: [from_id: {"does not exist", _}]}} ->
        Utils.error_embed("The system #{decorator} does not exist.")

      {:error, _} ->
        Utils.error_embed(
          "An unknown error occurred while accepting the friend request. Please try again."
        )
    end
  end

  defp reject_friend_request(%{system_identity: system_identity}, from_identity, decorator) do
    case Friendships.reject_request(from_identity, system_identity) do
      :ok ->
        Utils.success_embed("You rejected the friend request from #{decorator}.")

      {:error, :not_requested} ->
        Utils.error_embed("You do not have an incoming friend request from #{decorator}.")

      {:error, %{errors: [from_id: {"does not exist", _}]}} ->
        Utils.error_embed("The system #{decorator} does not exist.")

      {:error, _} ->
        Utils.error_embed(
          "An unknown error occurred while rejecting the friend request. Please try again."
        )
    end
  end

  defp cancel_friend_request(%{system_identity: system_identity}, to_identity, decorator) do
    case Friendships.cancel_request(system_identity, to_identity) do
      :ok ->
        Utils.success_embed("You canceled the friend request to #{decorator}.")

      {:error, :not_requested} ->
        Utils.error_embed("You do not have an outgoing friend request to #{decorator}.")

      {:error, %{errors: [to_id: {"does not exist", _}]}} ->
        Utils.error_embed("The system #{decorator} does not exist.")

      {:error, _} ->
        Utils.error_embed(
          "An unknown error occurred while cancelling the friend request. Please try again."
        )
    end
  end

  @impl true
  def type, do: :slash

  @impl true
  def options,
    do: [
      %{
        name: "add",
        description: "Sends a friend request to a system by their ID, Discord ping, or username.",
        type: :sub_command,
        options: [
          %{
            name: "system-id",
            type: :string,
            min_length: 7,
            max_length: 7,
            description: "The ID of the system to send a friend request to.",
            required: false
          },
          %{
            name: "username",
            type: :string,
            min_length: 5,
            max_length: 16,
            description: "The username of the system to send a friend request to.",
            required: false
          },
          %{
            name: "discord",
            description: "The Discord ping of the user to send a friend request to.",
            type: :user,
            required: false
          }
        ]
      },
      %{
        name: "accept",
        description:
          "Accepts a friend request from a system by their ID, Discord ping, or username.",
        type: :sub_command,
        options: [
          %{
            name: "system-id",
            type: :string,
            min_length: 7,
            max_length: 7,
            description: "The ID of the system whose friend request to accept.",
            required: false
          },
          %{
            name: "username",
            type: :string,
            min_length: 5,
            max_length: 16,
            description: "The username of the system whose friend request to accept.",
            required: false
          },
          %{
            name: "discord",
            description: "The Discord ping of the user whose friend request to accept.",
            type: :user,
            required: false
          }
        ]
      },
      %{
        name: "reject",
        description:
          "Rejects a friend request from a system by their ID, Discord ping, or username.",
        type: :sub_command,
        options: [
          %{
            name: "system-id",
            type: :string,
            min_length: 7,
            max_length: 7,
            description: "The ID of the system whose friend request to reject.",
            required: false
          },
          %{
            name: "username",
            type: :string,
            min_length: 5,
            max_length: 16,
            description: "The username of the system whose friend request to reject.",
            required: false
          },
          %{
            name: "discord",
            description: "The Discord ping of the user whose friend request to reject.",
            type: :user,
            required: false
          }
        ]
      },
      %{
        name: "cancel",
        description:
          "Cancels a friend request to a system by their ID, Discord ping, or username.",
        type: :sub_command,
        options: [
          %{
            name: "system-id",
            type: :string,
            min_length: 7,
            max_length: 7,
            description: "The ID of the system whose friend request to cancel.",
            required: false
          },
          %{
            name: "username",
            type: :string,
            min_length: 5,
            max_length: 16,
            description: "The username of the system whose friend request to cancel.",
            required: false
          },
          %{
            name: "discord",
            description: "The Discord ping of the user whose friend request to cancel.",
            type: :user,
            required: false
          }
        ]
      },
      %{
        name: "remove",
        description: "Removes a friend.",
        type: :sub_command,
        options: [
          %{
            name: "system-id",
            type: :string,
            min_length: 7,
            max_length: 7,
            description: "The ID of the system to remove as a friend.",
            required: false
          },
          %{
            name: "username",
            type: :string,
            min_length: 5,
            max_length: 16,
            description: "The username of the system to remove as a friend.",
            required: false
          },
          %{
            name: "discord",
            description: "The Discord ping of the user to remove as a friend.",
            type: :user,
            required: false
          }
        ]
      },
      %{
        name: "list",
        description: "Lists your friends.",
        type: :sub_command
      },
      %{
        name: "list-requests",
        description: "Lists your incoming and outgoing friend requests.",
        type: :sub_command
      },
      %{
        name: "trust",
        description: "Turns a friend into a \"trusted friend\".",
        type: :sub_command,
        options: [
          %{
            name: "system-id",
            type: :string,
            min_length: 7,
            max_length: 7,
            description: "The ID of the system to trust.",
            required: false
          },
          %{
            name: "username",
            type: :string,
            min_length: 5,
            max_length: 16,
            description: "The username of the system to trust.",
            required: false
          },
          %{
            name: "discord",
            description: "The Discord ping of the user to trust.",
            type: :user,
            required: false
          }
        ]
      },
      %{
        name: "untrust",
        description: "Turns a \"trusted friend\" into a regular friend.",
        type: :sub_command,
        options: [
          %{
            name: "system-id",
            type: :string,
            min_length: 7,
            max_length: 7,
            description: "The ID of the system to untrust.",
            required: false
          },
          %{
            name: "username",
            type: :string,
            min_length: 5,
            max_length: 16,
            description: "The username of the system to untrust.",
            required: false
          },
          %{
            name: "discord",
            description: "The Discord ping of the user to untrust.",
            type: :user,
            required: false
          }
        ]
      }
    ]
end
