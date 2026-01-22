defmodule Octocon.Friendships do
  @moduledoc """
  The Friendships context.

  This module represents the data layer for working with friendships between users.

  Most operations require system identities. See `Octocon.Accounts` for more information on system identities.
  """

  @query_concurrency 10

  import Ecto.Query, warn: false

  alias Octocon.{
    Accounts,
    Alters.Alter,
    Friendships.Friendship,
    Friendships.Request,
    Fronts,
    Fronts.CurrentFront,
    Repo
  }

  @doc """
  Returns the friendship status between two users with the given identities, or `nil` if no friendship exists.
  """
  def get_friendship(user_identity, friend_identity) do
    user_id = Accounts.id_from_system_identity(user_identity, :system)
    friend_id = Accounts.id_from_system_identity(friend_identity, :system)

    friendship =
      from(
        f in Friendship,
        where: f.user_id == ^user_id and f.friend_id == ^friend_id,
        select: f
      )
      |> Repo.one_global()

    friend =
      from(
        u in Octocon.Accounts.User,
        where: u.id == ^friend_id,
        select: struct(u, [:username, :description, :discord_id, :id, :avatar_url])
      )
      |> Repo.one_regional({:user, {:system, friend_id}})

    if friendship == nil do
      nil
    else
      %{
        friendship: friendship,
        friend: friend
      }
    end
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

    friendships =
      from(
        f in Friendship,
        where: f.user_id == ^user_id,
        select: f
      )
      |> Repo.all_global()

    friend_data =
      friendships
      |> Task.async_stream(
        fn %{friend_id: id} ->
          query =
            from(
              u in Octocon.Accounts.User,
              where: u.id == ^id,
              select: struct(u, [:avatar_url, :username, :discord_id, :id])
            )

          Repo.one_regional(query, {:user, {:system, id}})
        end,
        max_concurrency: @query_concurrency
      )
      |> Enum.filter(fn {:ok, user} -> user != nil end)
      |> Enum.into(%{}, fn {:ok, user} -> {user.id, user} end)

    friendships
    |> Enum.map(fn friendship ->
      %{
        friendship: friendship,
        friend: Map.get(friend_data, friendship.friend_id)
      }
    end)
    |> Enum.filter(fn f -> f.friend != nil end)
    |> Enum.sort_by(& &1.friendship.since, {:desc, DateTime})
  end

  @doc """
  Returns a list of all friendships for the given user identity, as well as a list of all fronting alters for each friend.
  """
  def list_friendships_guarded(user_identity) do
    friendships = list_friendships(user_identity)
    friendship_ids = Enum.map(friendships, fn f -> f.friend.id end)

    fronts =
      friendship_ids
      |> Task.async_stream(
        fn id ->
          query =
            from(
              f in CurrentFront,
              where: f.user_id == ^id,
              select: struct(f, [:id, :user_id, :alter_id, :comment, :time_start])
            )

          Repo.all_regional(query, {:user, {:system, id}})
        end,
        max_concurrency: @query_concurrency
      )
      |> Enum.map(fn {:ok, fronts} -> fronts end)
      |> List.flatten()
      |> Enum.map(&CurrentFront.to_front/1)

    alters =
      friendship_ids
      |> Task.async_stream(
        fn id ->
          alter_ids = Enum.filter(fronts, fn f -> f.user_id == id end) |> Enum.map(& &1.alter_id)

          query =
            from(
              a in Alter,
              where: a.user_id == ^id and a.id in ^alter_ids,
              select:
                struct(a, [
                  :id,
                  :user_id,
                  :name,
                  :avatar_url,
                  :pronouns,
                  :color,
                  :security_level,
                  :description
                ])
            )

          Repo.all_regional(query, {:user, {:system, id}})
        end,
        max_concurrency: @query_concurrency
      )
      |> Enum.map(fn {:ok, alters} -> alters end)
      |> Enum.into([])
      |> List.flatten()

    fronts =
      fronts
      |> Enum.map(fn front ->
        %{
          front: front,
          alter:
            alters
            |> Enum.find(fn alter ->
              alter.id == front.alter_id and alter.user_id == front.user_id
            end)
        }
      end)

    friendships
    |> Task.async_stream(
      fn friendship ->
        fronting =
          fronts
          |> Enum.filter(fn front ->
            front.front.user_id == friendship.friend.id
          end)
          |> then(fn fronts ->
            friendship_level =
              get_friendship({:system, friendship.friend.id}, user_identity).friendship.level

            Fronts.currently_fronting_hoisted(
              {:system, friendship.friend.id},
              friendship_level,
              fronts
            )
          end)

        Map.put(
          friendship,
          :fronting,
          fronting
        )
      end,
      max_concurrency: @query_concurrency
    )
    |> Enum.map(fn {:ok, friendships} -> friendships end)
    |> Enum.into([])
  end

  @doc """
  Returns whether or not a friendship exists between two users with the given identities.
  """
  def friendship_exists?(left_identity, right_identity) do
    left_id = Accounts.id_from_system_identity(left_identity, :system)
    right_id = Accounts.id_from_system_identity(right_identity, :system)

    from(
      f in Friendship,
      where: f.user_id == ^left_id and f.friend_id == ^right_id
    )
    |> Repo.one_global()
    |> case do
      nil -> false
      _ -> true
    end
  end

  @doc """
  Creates a new friendship entry between two users with the given identities.
  """
  def create_friendship(attrs \\ %{}) do
    %Friendship{}
    |> Friendship.changeset(attrs)
    |> Repo.insert_global()
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
      |> Repo.one_global()

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
    |> Repo.update_global()
  end

  def delete_friendship(%Friendship{} = friendship) do
    Repo.delete_global(friendship)
    Repo.delete_global(%Friendship{user_id: friendship.friend_id, friend_id: friendship.user_id})
  end

  @doc """
  Builds a changeset based on the given `Octocon.Friendships.Friendship` struct and `attrs` to change.
  """
  def change_friendship(%Friendship{} = friendship, attrs \\ %{}) do
    Friendship.changeset(friendship, attrs)
  end

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
    |> Repo.one_global()
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
    |> Repo.all_global()
    |> case do
      [] -> false
      _ -> true
    end
  end

  @doc """
  Links two users as friends, creating bidirectional friendship entries between them.
  """
  def link_friends(from_identity, to_identity) do
    from_id = Accounts.id_from_system_identity(from_identity, :system)
    to_id = Accounts.id_from_system_identity(to_identity, :system)

    %Friendship{
      user_id: from_id,
      friend_id: to_id,
      since: DateTime.utc_now(:second)
    }
    |> change_friendship()
    |> Repo.insert_global()

    %Friendship{
      user_id: to_id,
      friend_id: from_id,
      since: DateTime.utc_now(:second)
    }
    |> change_friendship()
    |> Repo.insert_global()

    delete_friend_requests(from_identity, to_identity)

    :ok
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

    if friendship_exists?(user_identity, friend_identity) do
      from(
        f in Friendship,
        where: f.user_id == ^user_id and f.friend_id == ^friend_id
      )
      |> Repo.delete_all_global()

      from(
        f in Friendship,
        where: f.user_id == ^friend_id and f.friend_id == ^user_id
      )
      |> Repo.delete_all_global()

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
    else
      {:error, :not_friends}
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

      Accounts.get_user({:system, Accounts.id_from_system_identity(to_identity, :system)}) == nil ->
        {:error, :no_user}

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

      Accounts.get_user({:system, Accounts.id_from_system_identity(to_identity, :system)}) == nil ->
        {:error, :no_user}

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
          |> Repo.insert_global()

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

    request =
      from(r in Request,
        where: r.from_id == ^user_id and r.to_id == ^friend_id
      )
      |> Repo.one_global()

    if request == nil do
      nil
    else
      user =
        from(
          u in Octocon.Accounts.User,
          where: u.id == ^request.to_id,
          select: struct(u, [:username, :discord_id, :id, :avatar_url])
        )
        |> Repo.one_regional({:user, {:system, request.to_id}})

      %{
        request: request,
        to: user
      }
    end
  end

  @doc """
  Gets the incoming friend request from one user to another, if it exists.
  """
  def get_incoming_friend_request(user_identity, friend_identity) do
    user_id = Accounts.id_from_system_identity(user_identity, :system)
    friend_id = Accounts.id_from_system_identity(friend_identity, :system)

    request =
      from(r in Request,
        where: r.from_id == ^user_id and r.to_id == ^friend_id
      )
      |> Repo.one_global()

    if request == nil do
      nil
    else
      user =
        from(
          u in Octocon.Accounts.User,
          where: u.id == ^request.from_id,
          select: struct(u, [:username, :discord_id, :id, :avatar_url])
        )
        |> Repo.one_regional({:user, {:system, request.from_id}})

      %{
        request: request,
        from: user
      }
    end
  end

  @doc """
  Gets all outgoing friend requests associated with the given user identity.
  """
  def outgoing_friend_requests(user_identity) do
    user_id = Accounts.id_from_system_identity(user_identity, :system)

    requests =
      from(
        r in Request,
        where: r.from_id == ^user_id,
        select: r
      )
      |> Repo.all_global()

    to_ids = Enum.map(requests, & &1.to_id)

    users =
      to_ids
      |> Enum.map(fn id ->
        query =
          from(
            u in Octocon.Accounts.User,
            where: u.id == ^id,
            select: struct(u, [:username, :discord_id, :id, :avatar_url])
          )

        {query, {:system, id}}
      end)
      |> Enum.map(fn {query, system_identity} ->
        Repo.one_regional(query, {:user, system_identity})
      end)
      |> Enum.into(%{}, fn user -> {user.id, user} end)

    requests
    |> Enum.map(fn request ->
      %{
        request: request,
        to: Map.get(users, request.to_id)
      }
    end)
    |> Enum.sort_by(& &1.request.date_sent, {:desc, DateTime})
  end

  @doc """
  Gets all incoming friend requests associated with the given user identity.
  """
  def incoming_friend_requests(user_identity) do
    user_id = Accounts.id_from_system_identity(user_identity, :system)

    requests =
      from(
        r in Request,
        where: r.to_id == ^user_id,
        select: r
      )
      |> Repo.all_global()

    from_ids = Enum.map(requests, & &1.from_id)

    users =
      from_ids
      |> Enum.map(fn id ->
        query =
          from(
            u in Octocon.Accounts.User,
            where: u.id == ^id,
            select: struct(u, [:username, :discord_id, :id, :avatar_url])
          )

        {query, {:system, id}}
      end)
      |> Enum.map(fn {query, system_identity} ->
        Repo.one_regional(query, {:user, system_identity})
      end)
      |> Enum.into(%{}, fn user -> {user.id, user} end)

    requests
    |> Enum.map(fn request ->
      %{
        request: request,
        from: Map.get(users, request.from_id)
      }
    end)
    |> Enum.sort_by(& &1.request.date_sent, {:desc, DateTime})
  end

  @doc """
  Deletes all friend requests between two users with the given identities.
  """
  def delete_friend_requests(left_identity, right_identity) do
    left_id = Accounts.id_from_system_identity(left_identity, :system)
    right_id = Accounts.id_from_system_identity(right_identity, :system)

    from(
      r in Request,
      where: r.from_id == ^left_id and r.to_id == ^right_id
    )
    |> Repo.delete_all_global()

    from(
      r in Request,
      where: r.from_id == ^right_id and r.to_id == ^left_id
    )
    |> Repo.delete_all_global()
  end

  @doc """
  Builds a changeset based on the given `Octocon.Friendships.Request` struct and `attrs` to change.
  """
  def change_friend_request(%Request{} = request, attrs \\ %{}) do
    Request.changeset(request, attrs)
  end
end
