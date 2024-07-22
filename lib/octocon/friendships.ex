defmodule Octocon.Friendships do
  @moduledoc """
  The Friendships context.

  This module represents the data layer for working with friendships between users.

  Most operations require system identities. See `Octocon.Accounts` for more information on system identities.
  """

  import Ecto.Query, warn: false
  alias Octocon.Alters.Alter
  alias Octocon.Repo

  alias Octocon.Accounts

  alias Octocon.Friendships.Friendship
  alias Octocon.Fronts
  alias Octocon.Fronts.Front

  @doc """
  Returns the friendship status between two users with the given identities, or `nil` if no friendship exists.
  """
  def get_friendship(user_identity, friend_identity) do
    user_id = Accounts.id_from_system_identity(user_identity, :system)
    friend_id = Accounts.id_from_system_identity(friend_identity, :system)

    from(
      f in Friendship,
      where: f.user_id == ^user_id and f.friend_id == ^friend_id,
      join: u in Octocon.Accounts.User,
      on: u.id == f.friend_id,
      select: %{
        friendship: f,
        friend: struct(u, [:username, :description, :discord_id, :id, :avatar_url])
      }
    )
    |> Repo.one()
  end

  @doc """
  Returns the friendship status between two users with the given identities, or `nil` if no friendship exists.

  This function also includes a list of all fronting alters for the friend, guarded by the current friendship level.
  """
  def get_friendship_guarded(user_identity, friend_identity) do
    case get_friendship(user_identity, friend_identity) do
      nil ->
        nil

      friendship ->
        Map.put(
          friendship,
          :fronting,
          Fronts.currently_fronting_guarded(
            friend_identity,
            user_identity
          )
        )
    end
  end

  @doc """
  Returns a list of all friendships for the given user identity.
  """
  def list_friendships(user_identity) do
    user_id = Accounts.id_from_system_identity(user_identity, :system)

    from(
      f in Friendship,
      where: f.user_id == ^user_id,
      join: u in Octocon.Accounts.User,
      on: u.id == f.friend_id,
      select: %{
        friendship: f,
        friend: struct(u, [:avatar_url, :username, :discord_id, :id])
      },
      order_by: [desc: f.since],
      order_by: [desc: f.level]
    )
    |> Repo.all()
  end

  @doc """
  Returns a list of all friendships for the given user identity, as well as a list of all fronting alters for each friend.
  """
  def list_friendships_guarded(user_identity) do
    friendships = list_friendships(user_identity)

    fronting =
      from(
        ff in Front,
        where:
          is_nil(ff.time_end) and ff.user_id in ^Enum.map(friendships, fn f -> f.friend.id end),
        join: a in Alter,
        on: a.id == ff.alter_id and ff.user_id == a.user_id,
        select: %{
          front: ff,
          alter:
            struct(a, [:id, :name, :avatar_url, :pronouns, :color, :security_level, :description])
        }
      )
      |> Repo.all()

    Enum.map(friendships, fn friendship ->
      Map.put(
        friendship,
        :fronting,
        fronting
        |> Enum.filter(fn front ->
          front.front.user_id == friendship.friend.id
        end)
        |> then(fn fronts ->
          friendship_level = get_friendship({:system, friendship.friend.id}, user_identity).friendship.level
          Fronts.currently_fronting_hoisted(
            {:system, friendship.friend.id},
            friendship_level,
            fronts
          )
        end)
      )
    end)

    # |> Enum.map(fn friendship ->
    #   Map.put(friendship, :fronting, Fronts.currently_fronting_hoisted(friendship.friend.id, user_id, friendship.friendship.level, ""))
    # end)
  end

  @doc """
  Returns whether or not a friendship exists between two users with the given identities.
  """
  def friendship_exists?(left_identity, right_identity) do
    left_id = Accounts.id_from_system_identity(left_identity, :system)
    right_id = Accounts.id_from_system_identity(right_identity, :system)

    from(
      f in Friendship,
      where:
        (f.user_id == ^left_id and f.friend_id == ^right_id) or
          (f.user_id == ^right_id and f.friend_id == ^left_id)
    )
    |> Repo.exists?()
  end

  @doc """
  Creates a new friendship entry between two users with the given identities.
  """
  def create_friendship(attrs \\ %{}) do
    %Friendship{}
    |> Friendship.changeset(attrs)
    |> Repo.insert()
  end

  @doc """
  Returns the friendship level between two users with the given identities.
  """
  def get_friendship_level(left_identity, right_identity)
      when is_nil(left_identity) == nil or is_nil(right_identity),
      do: :none

  def get_friendship_level(left_identity, right_identity) do
    left_id = Accounts.id_from_system_identity(left_identity, :system)
    right_id = Accounts.id_from_system_identity(right_identity, :system)

    level =
      from(
        f in Friendship,
        where: f.user_id == ^left_id and f.friend_id == ^right_id,
        select: f.level
      )
      |> Repo.one()

    case level do
      nil -> :none
      level -> level
    end
  end

  @doc """
  Updates the `Octocon.Friendships.Friendship` struct with the given attributes.
  """
  def update_friendship(%Friendship{} = friendship, attrs) do
    friendship
    |> Friendship.changeset(attrs)
    |> Repo.update()
  end

  @doc """
  Deletes the given `Octocon.Friendships.Friendship` struct.
  """
  def delete_friendship(%Friendship{} = friendship) do
    Repo.delete(friendship)
    Repo.delete(%Friendship{user_id: friendship.friend_id, friend_id: friendship.user_id})
  end

  @doc """
  Builds a changeset based on the given `Octocon.Friendships.Friendship` struct and `attrs` to change.
  """
  def change_friendship(%Friendship{} = friendship, attrs \\ %{}) do
    Friendship.changeset(friendship, attrs)
  end

  alias Octocon.Friendships.Request

  @doc """
  Returns the friend request between two users with the given identities, or `nil` if no request exists.
  """
  def get_friend_request(from_identity, to_identity) do
    from_id = Accounts.id_from_system_identity(from_identity, :system)
    to_id = Accounts.id_from_system_identity(to_identity, :system)

    from(
      r in Request,
      where: r.from_id == ^from_id and r.to_id == ^to_id
    )
    |> Repo.one()
  end

  @doc """
  Returns whether or not a friend request has been sent from one user to another.
  """
  def friend_request_exists?(from_identity, to_identity) do
    from_id = Accounts.id_from_system_identity(from_identity, :system)
    to_id = Accounts.id_from_system_identity(to_identity, :system)

    from(
      r in Request,
      where: r.from_id == ^from_id and r.to_id == ^to_id
    )
    |> Repo.exists?()
  end

  @doc false
  def link_friends_internal(from_identity, to_identity) do
    from_id = Accounts.id_from_system_identity(from_identity, :system)
    to_id = Accounts.id_from_system_identity(to_identity, :system)

    transaction =
      Repo.transaction(fn ->
        %Friendship{
          user_id: from_id,
          friend_id: to_id,
          since: DateTime.utc_now(:second)
        }
        |> change_friendship()
        |> Repo.insert!()

        %Friendship{
          user_id: to_id,
          friend_id: from_id,
          since: DateTime.utc_now(:second)
        }
        |> change_friendship()
        |> Repo.insert!()
      end)

    case transaction do
      {:ok, _} ->
        delete_friend_requests(from_identity, to_identity)
        :ok

      {:error, _} ->
        {:error, :database}
    end
  end

  @doc """
  Links two users as friends, creating bidirectional friendship entries between them.

  **PROXIED**: If this function is executed on an **auxiliary** node, it will be proxied to a random **primary** node.
  """
  def link_friends(from_identity, to_identity) do
    Fly.Postgres.rpc_and_wait(__MODULE__, :link_friends_internal, [from_identity, to_identity])
  end

  @doc """
  Trusts a friend, upgrading their friendship level to `:trusted_friend`.
  """
  def trust_friend(user_identity, friend_identity) do
    case get_friendship(user_identity, friend_identity) do
      nil ->
        {:error, :not_friends}

      %{friendship: friendship} ->
        case update_friendship(friendship, %{level: :trusted_friend}) do
          {:ok, _} ->
            spawn(fn ->
              user_id = Accounts.id_from_system_identity(user_identity, :system)
              friend_id = Accounts.id_from_system_identity(friend_identity, :system)

              OctoconWeb.Endpoint.broadcast!("system:#{user_id}", "friend_trusted", %{
                friend_id: friend_id
              })
            end)

            :ok

          {:error, _} ->
            {:error, :database}
        end
    end
  end

  @doc """
  Untrusts a friend, downgrading their friendship level to `:friend`.
  """
  def untrust_friend(user_identity, friend_identity) do
    case get_friendship(user_identity, friend_identity) do
      nil ->
        {:error, :not_friends}

      %{friendship: friendship} ->
        case update_friendship(friendship, %{level: :friend}) do
          {:ok, _} ->
            spawn(fn ->
              user_id = Accounts.id_from_system_identity(user_identity, :system)
              friend_id = Accounts.id_from_system_identity(friend_identity, :system)

              OctoconWeb.Endpoint.broadcast!("system:#{user_id}", "friend_untrusted", %{
                friend_id: friend_id
              })
            end)

            :ok

          {:error, _} ->
            {:error, :database}
        end
    end
  end

  @doc """
  Removes a friendship between two users with the given identities.
  """
  def remove_friendship(user_identity, friend_identity) do
    user_id = Accounts.id_from_system_identity(user_identity, :system)
    friend_id = Accounts.id_from_system_identity(friend_identity, :system)

    result =
      from(
        f in Friendship,
        where:
          (f.user_id == ^user_id and f.friend_id == ^friend_id) or
            (f.user_id == ^friend_id and f.friend_id == ^user_id)
      )
      |> Repo.delete_all()

    case result do
      {0, _} ->
        {:error, :not_friends}

      {2, _} ->
        OctoconDiscord.Utils.send_dm(
          {:system, friend_id},
          "Friendship removed",
          "You are no longer friends with the system **#{user_id}**."
        )

        [
          {friend_id, user_id},
          {user_id, friend_id}
        ]
        |> Enum.each(fn {to, removed} ->
          spawn(fn ->
            OctoconWeb.Endpoint.broadcast!("system:#{to}", "friend_removed", %{
              friend_id: removed
            })
          end)
        end)

        :ok

      _ ->
        {:error, :database}
    end
  end

  @doc """
  Accepts a friend request from one user to another, linking them as friends.
  """
  def accept_request(from_identity, to_identity) do
    cond do
      friendship_exists?(from_identity, to_identity) ->
        {:error, :already_friends}

      friend_request_exists?(from_identity, to_identity) ->
        case link_friends(from_identity, to_identity) do
          :ok ->
            from_id = Accounts.id_from_system_identity(from_identity, :system)
            to_id = Accounts.id_from_system_identity(to_identity, :system)

            OctoconDiscord.Utils.send_dm(
              {:system, from_id},
              ":white_check_mark: Friend request accepted",
              "The system **#{to_id}** has accepted your friend request."
            )

            [
              {from_id, to_id},
              {to_id, from_id}
            ]
            |> Enum.each(fn {to, accepted} ->
              spawn(fn ->
                OctoconWeb.Endpoint.broadcast!(
                  "system:#{to}",
                  "friend_added",
                  get_friendship_guarded({:system, to}, {:system, accepted})
                  |> OctoconWeb.FriendJSON.data()
                )

                OctoconWeb.Endpoint.broadcast!("system:#{to}", "friend_request_removed", %{
                  system_id: accepted
                })
              end)
            end)

            :ok

          _ ->
            {:error, :database}
        end

      true ->
        {:error, :not_requested}
    end
  end

  @doc """
  Rejects a friend request from one user to another, removing the request.
  """
  def reject_request(from_identity, to_identity, _send_dm \\ false) do
    cond do
      friendship_exists?(from_identity, to_identity) ->
        {:error, :already_friends}

      friend_request_exists?(from_identity, to_identity) ->
        delete_friend_requests(from_identity, to_identity)

        from_id = Accounts.id_from_system_identity(from_identity, :system)
        to_id = Accounts.id_from_system_identity(to_identity, :system)

        [
          {from_id, to_id},
          {to_id, from_id}
        ]
        |> Enum.each(fn {to, removed} ->
          spawn(fn ->
            OctoconWeb.Endpoint.broadcast!("system:#{to}", "friend_request_removed", %{
              system_id: removed
            })
          end)
        end)

        :ok

      true ->
        {:error, :not_requested}
    end
  end

  @doc """
  Cancels a friend request from one user to another, removing the request.
  """
  def cancel_request(from_identity, to_identity),
    do: reject_request(from_identity, to_identity, true)

  @doc """
  Sends a friend request from one user to another, creating a new request entry.
  """
  def send_request(from_identity, to_identity) do
    cond do
      friendship_exists?(from_identity, to_identity) ->
        {:error, :already_friends}

      friend_request_exists?(from_identity, to_identity) ->
        {:error, :already_sent_request}

      friend_request_exists?(to_identity, from_identity) ->
        case link_friends(from_identity, to_identity) do
          :ok -> {:ok, :accepted}
          _ -> {:error, :database}
        end

      true ->
        from_id = Accounts.id_from_system_identity(from_identity, :system)
        to_id = Accounts.id_from_system_identity(to_identity, :system)

        req =
          %Request{
            from_id: from_id,
            to_id: to_id,
            date_sent: DateTime.utc_now(:second)
          }
          |> change_friend_request()
          |> Repo.insert()

        case req do
          {:ok, _} ->
            OctoconDiscord.Utils.send_dm(
              {:system, to_id},
              ":mailbox_with_mail: New friend request!",
              "The system **#{from_id}** has sent you a friend request."
            )

            spawn(fn ->
              %{request: request, from: from} =
                get_incoming_friend_request(from_identity, to_identity)

              OctoconWeb.Endpoint.broadcast!(
                "system:#{to_id}",
                "friend_request_received",
                %{
                  request: request,
                  system: from
                }
                |> OctoconWeb.FriendRequestJSON.data()
              )
            end)

            spawn(fn ->
              %{request: request, to: to} =
                get_outgoing_friend_request(from_identity, to_identity)

              OctoconWeb.Endpoint.broadcast!(
                "system:#{from_id}",
                "friend_request_sent",
                %{
                  request: request,
                  system: to
                }
                |> OctoconWeb.FriendRequestJSON.data()
              )
            end)

            {:ok, :sent}

          {:error, error} ->
            {:error, error}
        end
    end
  end

  @doc """
  Gets the outgoing friend request from one user to another, if it exists.
  """
  def get_outgoing_friend_request(user_identity, friend_identity) do
    user_id = Accounts.id_from_system_identity(user_identity, :system)
    friend_id = Accounts.id_from_system_identity(friend_identity, :system)

    from(
      r in Request,
      where: r.from_id == ^user_id and r.to_id == ^friend_id,
      join: u in Octocon.Accounts.User,
      on: u.id == r.to_id,
      select: %{request: r, to: struct(u, [:username, :discord_id, :id, :avatar_url])}
    )
    |> Repo.one()
  end

  @doc """
  Gets the incoming friend request from one user to another, if it exists.
  """
  def get_incoming_friend_request(user_identity, friend_identity) do
    user_id = Accounts.id_from_system_identity(user_identity, :system)
    friend_id = Accounts.id_from_system_identity(friend_identity, :system)

    from(
      r in Request,
      where: r.from_id == ^user_id and r.to_id == ^friend_id,
      join: u in Octocon.Accounts.User,
      on: u.id == r.from_id,
      select: %{request: r, from: struct(u, [:username, :discord_id, :id, :avatar_url])}
    )
    |> Repo.one()
  end

  @doc """
  Gets all outgoing friend requests associated with the given user identity.
  """
  def outgoing_friend_requests(user_identity) do
    user_id = Accounts.id_from_system_identity(user_identity, :system)

    from(
      r in Request,
      where: r.from_id == ^user_id,
      join: u in Octocon.Accounts.User,
      on: u.id == r.to_id,
      select: %{request: r, to: struct(u, [:username, :discord_id, :id, :avatar_url])},
      order_by: [desc: r.date_sent]
    )
    |> Repo.all()
  end

  @doc """
  Gets all incoming friend requests associated with the given user identity.
  """
  def incoming_friend_requests(user_identity) do
    user_id = Accounts.id_from_system_identity(user_identity, :system)

    from(
      r in Request,
      where: r.to_id == ^user_id,
      join: u in Octocon.Accounts.User,
      on: u.id == r.from_id,
      select: %{request: r, from: struct(u, [:username, :discord_id, :id, :avatar_url])},
      order_by: [desc: r.date_sent]
    )
    |> Repo.all()
  end

  @doc """
  Deletes all friend requests between two users with the given identities.
  """
  def delete_friend_requests(left_identity, right_identity) do
    left_id = Accounts.id_from_system_identity(left_identity, :system)
    right_id = Accounts.id_from_system_identity(right_identity, :system)

    from(
      r in Request,
      where:
        (r.from_id == ^left_id and r.to_id == ^right_id) or
          (r.from_id == ^right_id and r.to_id == ^left_id)
    )
    |> Repo.delete_all()
  end

  @doc """
  Builds a changeset based on the given `Octocon.Friendships.Request` struct and `attrs` to change.
  """
  def change_friend_request(%Request{} = request, attrs \\ %{}) do
    Request.changeset(request, attrs)
  end
end
