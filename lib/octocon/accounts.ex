defmodule Octocon.Accounts do
  @moduledoc """
  The Accounts context.

  This module represents the data layer for working with user accounts. Almost all operations
  require a system "identity", which is a tuple of the form:

  - `{:system, system_id}`: References a user by their system ID (7-character, alphanumeric lowercase string).
  - `{:discord, discord_id}`: References a user by their Discord ID.
  """

  import Ecto.Query, warn: false

  alias Octocon.Accounts.{
    Field,
    User,
    UserRegistry
  }

  alias Octocon.Alters.Alter

  alias Octocon.Journals.{
    AlterJournalEntry,
    GlobalJournalAlters,
    GlobalJournalEntry
  }

  alias Octocon.Repo

  alias OctoconWeb.SystemJSON

  defp unwrap_system_identity_where(system_identity, extra \\ []) do
    case system_identity do
      {:system, system_id} -> [id: system_id] |> Keyword.merge(extra)
      {:discord, discord_id} -> [discord_id: discord_id] |> Keyword.merge(extra)
      {:username, username} -> [username: username] |> Keyword.merge(extra)
      {:email, email} -> [email: email] |> Keyword.merge(extra)
      {:apple, apple_id} -> [apple_id: apple_id] |> Keyword.merge(extra)
      {:google, google_id} -> [google_id: google_id] |> Keyword.merge(extra)
    end
  end

  # def unwrap_system_identity_where(system_identity, extra \\ []) do
  #   case system_identity do
  #     {:system, system_id} ->
  #       [id: system_id] |> Keyword.merge(extra)

  #     {:discord, _} = identity ->
  #       [id: Accounts.id_from_system_identity(identity, :system)]
  #       |> Keyword.merge(extra)
  #   end
  # end

  def get_user_registry(system_identity) do
    query =
      case system_identity do
        {:system, system_id} ->
          UserRegistry
          |> where([ur], ur.user_id == ^system_id)

        {:discord, discord_id} ->
          UserRegistry
          |> where([ur], ur.discord_id == ^discord_id)

        {:email, email} ->
          UserRegistry
          |> where([ur], ur.email == ^email)

        {:username, username} ->
          UserRegistry
          |> where([ur], ur.username == ^username)

        {:apple, apple_id} ->
          UserRegistry
          |> where([ur], ur.apple_id == ^apple_id)

        {:google, google_id} ->
          UserRegistry
          |> where([ur], ur.google_id == ^google_id)
      end
      |> select([ur], ur)

    Repo.one_global(query)
  end

  def delete_user_registry(system_identity) do
    case get_user_registry(system_identity) do
      nil ->
        {:error, :not_found}

      user_registry ->
        Repo.delete_global(user_registry)
    end
  end

  def user_exists?(system_identity) do
    get_user_registry(system_identity) != nil
  end

  def region_for_user(system_identity) do
    case get_user_registry(system_identity) do
      nil -> nil
      %UserRegistry{region: region} -> String.to_existing_atom(region)
    end
  end

  @doc """
  Returns the total number of users in the database.
  """
  def count do
    Octocon.Repo.aggregate(UserRegistry, :count, prefix: :global, consistency: :local_one)
  end

  @doc """
  Given a system identity, returns the desired ID. This does not query the database if the desired ID type was already given.

  ## Examples

      iex> id_from_system_identity({:system, "abcdefg"}, :system)
      "abcdefg"

      iex> id_from_system_identity({:discord, "123456789"}, :discord)
      "123456789"

      iex> id_from_system_identity({:system, "abcdefg"}, :discord)
      "123456789" # This will **query the database** for the Discord ID of the system with the ID "abcdefg".

      iex> id_from_system_identity({:discord, "123456789"}, :system)
      "abcdefg" # This will **query the database** for the system ID of the user with the Discord ID "123456789".
  """
  def id_from_system_identity(system_identity, type)

  # {:user, user_id} is an alias for {:system, user_id}
  def id_from_system_identity({:user, user_id}, type),
    do: id_from_system_identity({:system, user_id}, type)

  # When we're asking for the type we already have, just return the given ID.
  def id_from_system_identity({type, id}, type), do: id

  def id_from_system_identity(system_identity, type) do
    registry = get_user_registry(system_identity)

    if registry == nil do
      nil
    else
      case type do
        :system -> registry.user_id
        :discord -> registry.discord_id
        :email -> registry.email
        :username -> registry.username
        :apple -> registry.apple_id
        :google -> registry.google_id
      end
    end
  end

  @doc """
  Given a system identity, returns the `Octocon.Accounts.User` struct associated with it. Returns `nil` if no user is found.
  """
  def get_user(system_identity) do
    where = unwrap_system_identity_where(system_identity)

    query =
      User
      |> where(^where)

    Repo.one_regional(query, {:user, system_identity})
  end

  @doc """
  Given a system identity, returns the user associated with it. Raises an `Ecto.NoResultsError` if no user is found.
  """
  def get_user!(system_identity) do
    case get_user(system_identity) do
      nil -> raise Ecto.NoResultsError
      user -> user
    end
  end

  @doc """
  Given a username, returns the `Octocon.Accounts.User` struct associated with it. Returns `nil` if no user is found.
  """
  def get_user_by_username(username) do
    query =
      from u in User,
        where: u.username == ^username

    Repo.one_regional(query, {:user, {:username, username}})
  end

  @doc """
  Given a username, returns the ID of the user associated with it. Returns `nil` if no user is found.
  """
  def get_user_id_by_username(username) do
    id_from_system_identity({:username, username}, :system)
  end

  def allocate_uuid do
    uuid = Octocon.Accounts.User.generate_uuid()

    case region_for_user({:system, uuid}) do
      nil ->
        uuid

      _ ->
        allocate_uuid()
    end
  end

  def register_user(user_id, region, attrs \\ %{}) do
    %UserRegistry{
      user_id: user_id,
      region: to_string(region)
    }
    |> UserRegistry.changeset(attrs)
    |> Repo.insert_global()
  end

  def update_user_registry(user_id, attrs \\ %{}) do
    case get_user_registry({:system, user_id}) do
      nil ->
        {:error, :not_found}

      user_registry ->
        user_registry
        |> UserRegistry.changeset(attrs)
        |> Repo.update_global()
    end
  end

  @doc """
  Creates a user given the provided `email` address and extra `attrs`.
  """
  def create_user_from_email(email, attrs \\ %{}) do
    if user_exists?({:email, email}) do
      {:error, :user_exists}
    else
      current_region = Octocon.ClusterUtils.current_db_region()
      uuid = allocate_uuid()

      user =
        email
        |> User.create_from_email_changeset(uuid, attrs)
        |> Repo.insert_regional({:region, current_region})

      case user do
        {:ok, user_struct} = result ->
          register_user(user_struct.id, current_region, %{
            email: user_struct.email
          })

          result

        err ->
          err
      end
    end
  end

  @doc """
  Creates a user given the provided `discord_id` and extra `attrs`.
  """
  def create_user_from_discord(discord_id, attrs \\ %{}) do
    if user_exists?({:discord, discord_id}) do
      {:error, :user_exists}
    else
      current_region = Octocon.ClusterUtils.current_db_region()
      uuid = allocate_uuid()
      OctoconDiscord.ProxyCache.invalidate(discord_id)

      user =
        discord_id
        |> User.create_from_discord_changeset(uuid, attrs)
        |> Repo.insert_regional({:region, current_region})

      case user do
        {:ok, user_struct} = result ->
          register_user(user_struct.id, current_region, %{
            discord_id: user_struct.discord_id
          })

          result

        err ->
          err
      end
    end
  end

  @doc """
  Creates a user given the provided `apple_id` and extra `attrs`.
  """
  def create_user_from_apple(apple_id, attrs \\ %{}) do
    if user_exists?({:apple, apple_id}) do
      {:error, :user_exists}
    else
      current_region = Octocon.ClusterUtils.current_db_region()
      uuid = allocate_uuid()

      user =
        apple_id
        |> User.create_from_apple_changeset(uuid, attrs)
        |> Repo.insert_regional({:region, current_region})

      case user do
        {:ok, user_struct} = result ->
          register_user(user_struct.id, current_region, %{
            apple_id: user_struct.apple_id
          })

          result

        err ->
          err
      end
    end
  end

  @doc """
  Links a Discord ID to an existing user.

  This will return:
  - {:error, changeset} when the Discord ID is already linked to another account
  - {:error, :already_linked} when the user is already linked to a Discord account
  """
  def link_discord_to_user(%User{} = user, discord_id) do
    cond do
      user.discord_id != nil ->
        {:error, :already_linked}

      user_exists?({:discord, discord_id}) ->
        {:error, :user_exists}

      true ->
        user
        |> User.update_changeset(%{discord_id: discord_id})
        |> Repo.update_regional({:user, {:system, user.id}})
        |> case do
          {:ok, value} ->
            OctoconDiscord.ProxyCache.invalidate(discord_id)

            spawn(fn ->
              update_user_registry(user.id, %{discord_id: discord_id})
            end)

            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "discord_account_linked", %{
                discord_id: discord_id
              })
            end)

            {:ok, value}

          {:error, changeset} ->
            {:error, changeset}
        end
    end
  end

  def link_discord_to_user(system_identity, discord_id) do
    user = get_user!(system_identity)
    link_discord_to_user(user, discord_id)
  end

  @doc """
  Links an email to an existing user. Accepts either a `User` struct directly or a system identity.

  This will return:
  - {:error, changeset} when the email is already linked to another account.
  - {:error, :already_linked} when the user is already linked to an email.
  - {:error, :cannot_unlink} when the user is not linked to a Discord account (all accounts must be linked to at least one authentication method).
  """
  def link_email_to_user(%User{} = user, email) do
    cond do
      user.email != nil ->
        {:error, :already_linked}

      user_exists?({:email, email}) ->
        {:error, :user_exists}

      true ->
        user
        |> User.update_changeset(%{email: email})
        |> Repo.update_regional({:user, {:system, user.id}})
        |> case do
          {:ok, value} ->
            spawn(fn ->
              update_user_registry(user.id, %{email: email})
            end)

            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "google_account_linked", %{
                email: email
              })
            end)

            {:ok, value}

          {:error, changeset} ->
            {:error, changeset}
        end
    end
  end

  def link_email_to_user(system_identity, email) do
    user = get_user!(system_identity)
    link_email_to_user(user, email)
  end

  @doc """
  Links an Apple account to an existing user. Accepts either a `User` struct directly or a system identity.

  This will return:
  - {:error, changeset} when the Apple user ID is already linked to another account.
  - {:error, :already_linked} when the user is already linked to an Apple account.
  - {:error, :cannot_unlink} when the user is not linked to a different account type (all accounts must be linked to at least one authentication method).
  """
  def link_apple_to_user(%User{} = user, apple_id) do
    cond do
      user.apple_id != nil ->
        {:error, :already_linked}

      user_exists?({:apple, apple_id}) ->
        {:error, :user_exists}

      true ->
        user
        |> User.update_changeset(%{apple_id: apple_id})
        |> Repo.update_regional({:user, {:system, user.id}})
        |> case do
          {:ok, value} ->
            spawn(fn ->
              update_user_registry(user.id, %{apple_id: apple_id})
            end)

            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "apple_account_linked", %{
                apple_id: apple_id
              })
            end)

            {:ok, value}

          {:error, changeset} ->
            {:error, changeset}
        end
    end
  end

  def link_apple_to_user(system_identity, email) do
    user = get_user!(system_identity)
    link_apple_to_user(user, email)
  end

  @doc """
  Unlinks an email from an existing user. Accepts either a `User` struct directly or a system identity.

  This will return:
  - {:error, :not_linked} when the user is not linked to an email.
  - {:error, :cannot_unlink} when the user is not linked to a Discord account (all accounts must be linked to at least one authentication method).
  """
  def unlink_email_from_user(%User{} = user) do
    cond do
      user.email == nil ->
        {:error, :not_linked}

      user.discord_id == nil && user.apple_id == nil ->
        {:error, :cannot_unlink}

      true ->
        user
        |> User.update_changeset(%{email: nil})
        |> Repo.update_regional({:user, {:system, user.id}})
        |> case do
          {:ok, value} ->
            spawn(fn ->
              update_user_registry(user.id, %{email: nil})
            end)

            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "google_account_unlinked", %{})
            end)

            {:ok, value}

          {:error, changeset} ->
            {:error, changeset}
        end
    end
  end

  def unlink_email_from_user(system_identity) do
    user = get_user!(system_identity)
    unlink_email_from_user(user)
  end

  @doc """
  Unlinks a Discord ID from an existing user. Accepts either a `User` struct directly or a system identity.

  This will return:
  - {:error, :not_linked} when the user is not linked to a Discord account.
  - {:error, :cannot_unlink} when the user is not linked to another account type (all accounts must be linked to at least one authentication method).
  """
  def unlink_discord_from_user(%User{} = user) do
    cond do
      user.discord_id == nil ->
        {:error, :not_linked}

      user.email == nil && user.apple_id == nil ->
        {:error, :cannot_unlink}

      true ->
        user
        |> User.update_changeset(%{discord_id: nil})
        |> Repo.update_regional({:user, {:system, user.id}})
        |> case do
          {:ok, value} ->
            spawn(fn ->
              update_user_registry(user.id, %{discord_id: nil})
            end)

            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "discord_account_unlinked", %{})
            end)

            {:ok, value}

          {:error, changeset} ->
            {:error, changeset}
        end
    end
  end

  def unlink_discord_from_user(system_identity) do
    user = get_user!(system_identity)
    unlink_discord_from_user(user)
  end

  @doc """
  Unlinks an Apple account from an existing user. Accepts either a `User` struct directly or a system identity.

  This will return:
  - {:error, :not_linked} when the user is not linked to an Apple account.
  - {:error, :cannot_unlink} when the user is not linked to another account type (all accounts must be linked to at least one authentication method).
  """
  def unlink_apple_from_user(%User{} = user) do
    cond do
      user.apple_id == nil ->
        {:error, :not_linked}

      user.email == nil && user.discord_id == nil ->
        {:error, :cannot_unlink}

      true ->
        user
        |> User.update_changeset(%{apple_id: nil})
        |> Repo.update_regional({:user, {:system, user.id}})
        |> case do
          {:ok, value} ->
            spawn(fn ->
              update_user_registry(user.id, %{apple_id: nil})
            end)

            spawn(fn ->
              OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "apple_account_unlinked", %{})
            end)

            {:ok, value}

          {:error, changeset} ->
            {:error, changeset}
        end
    end
  end

  def unlink_apple_from_user(system_identity) do
    user = get_user!(system_identity)
    unlink_apple_from_user(user)
  end

  @doc """
  Updates a `Octocon.Accounts.User` struct with the provided `attrs`.
  """
  def update_user(%User{} = user, attrs) do
    user
    |> User.update_changeset(attrs)
    |> Repo.update_regional({:user, {:system, user.id}})
    |> case do
      {:ok, value} ->
        spawn(fn ->
          OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "self_updated", %{
            data: SystemJSON.data_me(value)
          })
        end)

        {:ok, value}

      {:error, changeset} ->
        {:error, changeset}
    end
  end

  @doc """
  Returns the primary front of the user associated with the provided system identity. May be `nil` if no primary front is set.
  """
  def get_primary_front(system_identity) do
    where = unwrap_system_identity_where(system_identity)

    query =
      User
      |> where(^where)
      |> select([u], u.primary_front)

    Repo.one_regional(query, {:user, system_identity})
  end

  def get_proxy_cache_data(system_identity) do
    where = unwrap_system_identity_where(system_identity)

    query =
      User
      |> where(^where)
      |> select([u], [
        :primary_front,
        :discord_settings
      ])

    Repo.one_regional(query, {:user, system_identity})
  end

  @doc """
  Sets the primary front of the user associated with the provided system identity. May be `nil` to unset the primary front. Accepts
  either a `User` struct directly or a system identity.

  This must be used in place of `update_user/2` to ensure that the primary front is properly updated in the Discord cache.
  """
  def set_primary_front(identifier, alter_id)

  def set_primary_front(%User{} = user, alter_id) do
    result = update_user(user, %{primary_front: alter_id || nil})

    if match?({:ok, _}, result) do
      spawn(fn ->
        OctoconDiscord.ProxyCache.update_primary_front(user.discord_id, alter_id || nil)

        OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "primary_front", %{
          alter_id: alter_id || nil
        })
      end)
    end

    result
  end

  def set_primary_front(system_identity, alter_id) do
    get_user!(system_identity)
    |> set_primary_front(alter_id)
  end

  def update_discord_settings(%User{} = user, attrs) do
    settings = user.discord_settings || %Octocon.Accounts.DiscordSettings{}

    old_attrs =
      settings
      |> Map.from_struct()
      |> Map.put(
        :server_settings,
        (Map.get(settings, :server_settings) || [])
        |> Enum.map(&Map.from_struct/1)
      )

    result =
      user
      |> User.update_changeset(%{
        discord_settings: Map.merge(old_attrs, attrs)
      })
      |> Repo.update_regional({:user, {:system, user.id}})

    if match?({:ok, _}, result) do
      OctoconDiscord.ProxyCache.invalidate(user.discord_id)
    end

    result
  end

  def update_discord_settings(system_identity, attrs) do
    get_user!(system_identity)
    |> update_discord_settings(attrs)
  end

  def update_server_settings(%User{} = user, guild_id, settings) when is_binary(guild_id) do
    settings = Map.drop(settings, [:guild_id])

    old_discord_settings =
      (user.discord_settings || %Octocon.Accounts.DiscordSettings{}) |> Map.from_struct()

    old_settings =
      ((user.discord_settings || %Octocon.Accounts.DiscordSettings{}).server_settings || [])
      |> Enum.map(&Map.from_struct/1)

    result =
      if Enum.any?(old_settings, fn server ->
           server.guild_id == guild_id
         end) do
        old_settings
        |> Enum.map(fn server ->
          if server.guild_id == guild_id do
            Map.merge(server, settings)
          else
            server
          end
        end)
      else
        [Map.merge(%{guild_id: guild_id}, settings) | old_settings]
      end
      |> then(fn new_settings ->
        User.update_changeset(user, %{
          discord_settings: Map.merge(old_discord_settings, %{server_settings: new_settings})
        })
      end)
      |> Repo.update_regional({:user, {:system, user.id}})

    if match?({:ok, _}, result) do
      OctoconDiscord.ProxyCache.invalidate(user.discord_id)
    end

    result
  end

  def update_server_settings(system_identity, guild_id, settings) do
    get_user!(system_identity)
    |> update_server_settings(guild_id, settings)
  end

  @doc """
  Deletes the user associated with the provided system identity.
  """
  def delete_user(system_identity) do
    user = get_user!(system_identity)

    case Repo.delete_regional(user, {:user, {:system, user.id}}) do
      {:error, _} ->
        {:error, :not_deleted}

      {:ok, _} ->
        OctoconDiscord.ProxyCache.invalidate(user.discord_id)

        spawn(fn ->
          delete_user_registry({:system, user.id})
          delete_user_data(user.id)
        end)

        spawn(fn ->
          OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "account_deleted", %{})
        end)

        :ok
    end
  end

  defp delete_user_data(system_id) do
    from(
      a in Alter,
      where: a.user_id == ^system_id
    )
    |> Repo.delete_all_regional({:user, {:system, system_id}})

    from(
      at in Octocon.Tags.AlterTag,
      where: at.user_id == ^system_id
    )
    |> Repo.delete_all_regional({:user, {:system, system_id}})

    from(
      f in Octocon.Fronts.Front,
      where: f.user_id == ^system_id
    )
    |> Repo.delete_all_regional({:user, {:system, system_id}})

    from(
      f in Octocon.Fronts.CurrentFront,
      where: f.user_id == ^system_id
    )
    |> Repo.delete_all_regional({:user, {:system, system_id}})

    from(
      aj in Octocon.Journals.AlterJournalEntry,
      where: aj.user_id == ^system_id
    )
    |> Repo.delete_all_regional({:user, {:system, system_id}})

    from(
      aj in Octocon.Journals.GlobalJournalEntry,
      where: aj.user_id == ^system_id
    )
    |> Repo.delete_all_regional({:user, {:system, system_id}})

    from(
      p in Octocon.Polls.Poll,
      where: p.user_id == ^system_id
    )
    |> Repo.delete_all_regional({:user, {:system, system_id}})

    from(
      t in Octocon.Tags.Tag,
      where: t.user_id == ^system_id
    )
    |> Repo.delete_all_regional({:user, {:system, system_id}})

    from(
      gw in Octocon.Journals.GlobalJournalAlters,
      where: gw.user_id == ^system_id
    )
    |> Repo.delete_all_regional({:user, {:system, system_id}})

    from(
      f in Octocon.Friendships.Friendship,
      where: f.user_id == ^system_id
    )
    |> Repo.delete_all_global()

    # Delete reciprocal friendships (Scylla cannot directly DELETE from a secondary index)
    from(
      f in Octocon.Friendships.Friendship,
      where: f.friend_id == ^system_id
    )
    |> Repo.all_global()
    |> Enum.map(& &1.user_id)
    |> Enum.each(fn user_id ->
      from(
        f in Octocon.Friendships.Friendship,
        where: f.user_id == ^user_id and f.friend_id == ^system_id
      )
      |> Repo.delete_all_global()
    end)

    from(
      f in Octocon.Friendships.Request,
      where: f.from_id == ^system_id
    )
    |> Repo.delete_all_global()

    # Delete reciprocal friend requests (Scylla cannot directly DELETE from a secondary index)
    from(
      f in Octocon.Friendships.Request,
      where: f.to_id == ^system_id
    )
    |> Repo.all_global()
    |> Enum.map(& &1.from_id)
    |> Enum.each(fn from_id ->
      from(
        f in Octocon.Friendships.Request,
        where: f.from_id == ^from_id and f.to_id == ^system_id
      )
      |> Repo.delete_all_global()
    end)
  end

  @doc """
  Wipes all alters associated with the user associated with the provided system identity.

  This also resets the user's lifetime alter count to 0, so the next alter will be assigned ID 1.
  """
  def wipe_alters(system_identity) do
    OctoconDiscord.ProxyCache.invalidate(system_identity)

    user = get_user!(system_identity)

    q1 =
      from a in Alter,
        where: a.user_id == ^user.id

    q2 =
      from f in Octocon.Fronts.Front,
        where: f.user_id == ^user.id

    q3 =
      from f in Octocon.Journals.AlterJournalEntry,
        where: f.user_id == ^user.id

    q4 =
      from f in Octocon.Journals.GlobalJournalAlters,
        where: f.user_id == ^user.id

    q5 =
      from f in Octocon.Tags.AlterTag,
        where: f.user_id == ^user.id

    q6 =
      from f in Octocon.Fronts.CurrentFront,
        where: f.user_id == ^user.id

    [q1, q2, q3, q4, q5, q6]
    |> Enum.each(fn query ->
      Repo.delete_all_regional(query, {:user, {:system, user.id}})
    end)

    user
    |> User.update_changeset(%{primary_front: nil, lifetime_alter_count: 0})
    |> Repo.update_regional({:user, {:system, user.id}})

    spawn(fn ->
      OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "alters_wiped", %{})
    end)

    :ok
  rescue
    e -> {:error, e}
  end

  @doc """
  Builds a changeset based on the given `Octocon.Accounts.User` struct and `attrs` to change.
  """
  def change_user(%User{} = user, attrs \\ %{}) do
    User.update_changeset(user, attrs)
  end

  @doc false
  def get_user_proxy_map_old(system_identity) do
    where = unwrap_system_identity_where(system_identity)

    user = from(u in User, where: ^where) |> Repo.one_regional({:user, system_identity})

    alters =
      from(
        a in Alter,
        where: a.user_id == ^user.id,
        select: struct(a, [:id, :discord_proxies])
      )
      |> Repo.all_regional({:user, {:system, user.id}})
      |> Enum.filter(fn alter -> alter.discord_proxies != [] && alter.discord_proxies != nil end)

    Enum.reduce(alters, %{}, fn %{id: alter_id, discord_proxies: proxies}, acc ->
      # Skip if proxies is nil or empty
      Enum.reduce(proxies, acc, fn proxy, map ->
        Map.put(map, proxy, {user.id, alter_id})
      end)
    end)
  end

  @doc """
  Wipes all custom fields for the user with the provided system identity.
  """
  def wipe_fields(system_identity) do
    user = get_user!(system_identity)

    user
    |> User.update_changeset(%{fields: []})
    |> Repo.update_regional({:user, {:system, user.id}})
    |> wrap_fields_broadcast(system_identity)
  end

  @doc """
  Edits an existing custom field for the user with the provided system identity.
  """
  def edit_field(system_identity, id, data) do
    user = get_user!(system_identity)

    fields =
      (user.fields || [])
      |> Enum.map(fn field ->
        if field.id == id do
          struct(field, data)
        else
          field
        end
      end)

    user
    |> User.update_changeset(%{fields: fields})
    |> Repo.update_regional({:user, {:system, user.id}})
    |> wrap_fields_broadcast(system_identity)
  end

  @doc """
  Adds a new custom field to the user with the provided system identity.
  """
  def add_field(system_identity, data) do
    user = get_user!(system_identity)

    fields =
      (user.fields || []) ++
        [
          struct(
            %Field{
              id: Ecto.UUID.generate(),
              locked: false
            },
            data
          )
        ]

    user
    |> User.update_changeset(%{fields: fields})
    |> Repo.update_regional({:user, {:system, user.id}})
    |> wrap_fields_broadcast(system_identity)
  end

  @doc """
  Removes an existing custom field from the user with the provided system identity.
  """
  def remove_field(system_identity, id) do
    user = get_user!(system_identity)

    fields =
      (user.fields || [])
      |> Enum.reject(fn field -> field.id == id end)

    user
    |> User.update_changeset(%{fields: fields})
    |> Repo.update_regional({:user, {:system, user.id}})
    |> wrap_fields_broadcast(system_identity)
  end

  @doc """
  Relocates an existing custom field for the user with the provided system identity to the desired `index`.
  """
  def relocate_field(system_identity, id, index) do
    user = get_user!(system_identity)

    old_fields = user.fields || []
    field = Enum.find(old_fields, fn field -> field.id == id end)

    fields =
      old_fields
      |> Enum.reject(&(&1.id == id))
      |> List.insert_at(index, field)

    user
    |> User.update_changeset(%{fields: fields})
    |> Repo.update_regional({:user, {:system, user.id}})
    |> wrap_fields_broadcast(system_identity)
  end

  @doc """
  Returns the custom fields for the user with the provided system identity.
  """
  def get_user_fields(system_identity) do
    where = unwrap_system_identity_where(system_identity)

    query =
      User
      |> where(^where)
      |> select([u], u.fields)

    Repo.one_regional(query, {:user, system_identity})
  end

  defp wrap_fields_broadcast({:ok, _} = result, system_identity) do
    spawn(fn ->
      user_id = id_from_system_identity(system_identity, :system)

      OctoconWeb.Endpoint.broadcast!("system:#{user_id}", "fields_updated", %{
        fields:
          get_user_fields(system_identity) |> Enum.map(&Map.drop(&1, [:__meta__, :__struct__]))
      })
    end)

    result
  end

  defp wrap_fields_broadcast({:error, _} = result, _), do: result

  def wipe_encrypted_data(system_identity) do
    alias Octocon.Journals.{AlterJournalEntry, GlobalJournalAlters, GlobalJournalEntry}

    user = get_user!(system_identity)
    specifier = {:user, {:system, user.id}}

    {:ok, _} =
      user
      |> User.update_changeset(%{
        encryption_initialized: false,
        encryption_key_checksum: nil
      })
      |> Repo.update_regional(specifier)

    from(
      ja in GlobalJournalAlters,
      where: ja.user_id == ^user.id
    )
    |> Repo.delete_all_regional(specifier)

    from(
      j in GlobalJournalEntry,
      where: j.user_id == ^user.id
    )
    |> Repo.delete_all_regional(specifier)

    from(
      j in AlterJournalEntry,
      where: j.user_id == ^user.id
    )
    |> Repo.delete_all_regional(specifier)

    spawn(fn ->
      OctoconWeb.Endpoint.broadcast!("system:#{user.id}", "encrypted_data_wiped", %{})
    end)

    :ok
  rescue
    e -> {:error, e}
  end
end
