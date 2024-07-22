defmodule OctoconWeb.Utils.Systems do
  @moduledoc false

  import Phoenix.Controller, only: [json: 2]
  import Plug.Conn

  alias Octocon.Accounts
  alias Octocon.Accounts.User
  alias Octocon.Friendships

  @spec parse_system(Plug.Conn.t(), String.t()) ::
          {:noreply, Plug.Conn.t()} | {:self, User.t()} | {:other, User.t()}
  def parse_system(conn, id)

  def parse_system(conn, "me") do
    caller_id = conn.private[:guardian_default_resource]

    if caller_id == nil do
      {
        :noreply,
        conn
        |> put_status(:unauthorized)
        |> json(%{error: "You are not authenticated.", code: "unauthenticated"})
      }
    else
      system = Accounts.get_user!({:system, caller_id})
      {:self, system}
    end
  end

  def parse_system(conn, "id:" <> system_id) when byte_size(system_id) == 7 do
    caller_id = conn.private[:guardian_default_resource]

    case Accounts.get_user({:system, system_id}) do
      nil ->
        {:noreply,
         conn
         |> put_status(:not_found)
         |> json(%{error: "No system found with ID \"#{system_id}\""})}

      system when system.id == caller_id ->
        {:self, system}

      system ->
        {:other, system |> put_friend_status(caller_id)}
    end
  end

  def parse_system(conn, "username:" <> username)
      when byte_size(username) >= 3 and byte_size(username) <= 16 do
    caller_id = conn.private[:guardian_default_resource]

    case Accounts.get_user_by_username(username) do
      nil ->
        {:noreply,
         conn
         |> put_status(:not_found)
         |> json(%{error: "No system found with username \"#{username}\""})}

      system when system.id == caller_id ->
        {:self, system}

      system ->
        {:other, system |> put_friend_status(caller_id)}
    end
  end

  def parse_system(conn, "discord:" <> discord_id)
      when byte_size(discord_id) >= 16 and byte_size(discord_id) <= 22 do
    caller_id = conn.private[:guardian_default_resource]

    case Accounts.get_user({:discord, discord_id}) do
      nil ->
        {:noreply,
         conn
         |> put_status(:not_found)
         |> json(%{error: "No system found with Discord ID \"#{discord_id}\""})}

      system when system.id == caller_id ->
        {:self, system}

      system ->
        {:other, system |> put_friend_status(caller_id)}
    end
  end

  def parse_system(conn, _) do
    {:noreply,
     conn
     |> put_status(:bad_request)
     |> json(%{
       error:
         "Invalid format for system ID; must be one of: \"me\", \"id:<id>\", \"username:<username>\", \"discord:<discord_id>\""
     })}
  end

  defp put_friend_status(system, caller_id) when caller_id == nil, do: system
  defp put_friend_status(%User{} = system, caller_id) when system.id == caller_id, do: system

  defp put_friend_status(%User{} = system, caller_id) do
    system
    |> Map.put(
      :friend_status,
      Friendships.get_friendship_level({:system, caller_id}, {:system, system.id})
    )
  end
end
